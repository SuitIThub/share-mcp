package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"

	"share/internal/client"
	"share/internal/mcpserver"
	"share/internal/protocol"
)

const usage = `usage: share [-config path] <command> [args]

commands:
  pair <server-url> <code> [-name NAME]     pair this machine (as agent/sender)
  devices                                   list target devices
  send <files...> (--to A,B | --all) [--minutes N] [--message M]
  text "<text>" (--to A,B | --all)
  link <url> (--to A,B | --all)
  mcp                                       run the MCP server on stdio
`

type cli struct {
	cfgPath string
	stdout  io.Writer
	stderr  io.Writer
}

// parseInterleaved parses flags that may appear before, between or after positional args.
func parseInterleaved(fs *flag.FlagSet, args []string) ([]string, error) {
	var pos []string
	for {
		if err := fs.Parse(args); err != nil {
			return nil, err
		}
		args = fs.Args()
		if len(args) == 0 {
			return pos, nil
		}
		pos = append(pos, args[0])
		args = args[1:]
	}
}

func run(ctx context.Context, args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("share", flag.ContinueOnError)
	fs.SetOutput(stderr)
	cfgPath := fs.String("config", client.DefaultConfigPath(), "config file")
	fs.Usage = func() { fmt.Fprint(stderr, usage) }
	if err := fs.Parse(args); err != nil {
		return 2
	}
	rest := fs.Args()
	if len(rest) == 0 {
		fs.Usage()
		return 2
	}
	c := &cli{cfgPath: *cfgPath, stdout: stdout, stderr: stderr}
	switch rest[0] {
	case "pair":
		return c.pair(ctx, rest[1:])
	case "devices":
		return c.devices(ctx)
	case "send":
		return c.send(ctx, protocol.KindFiles, rest[1:])
	case "text":
		return c.send(ctx, protocol.KindText, rest[1:])
	case "link":
		return c.send(ctx, protocol.KindLink, rest[1:])
	case "mcp":
		return c.mcp(ctx)
	default:
		fs.Usage()
		return 2
	}
}

func (c *cli) fail(code int, format string, a ...any) int {
	fmt.Fprintf(c.stderr, "share: "+format+"\n", a...)
	return code
}

func (c *cli) loadAPI() (*client.API, error) {
	cfg, err := client.LoadConfig(c.cfgPath)
	if err != nil {
		return nil, fmt.Errorf("not paired (%v) – run 'share pair <server-url> <code>' first", err)
	}
	return client.NewAPI(cfg), nil
}

func (c *cli) pair(ctx context.Context, args []string) int {
	fs := flag.NewFlagSet("pair", flag.ContinueOnError)
	fs.SetOutput(c.stderr)
	host, _ := os.Hostname()
	name := fs.String("name", host+" Agent", "device name")
	pos, err := parseInterleaved(fs, args)
	if err != nil || len(pos) != 2 {
		return c.fail(2, "usage: share pair <server-url> <code> [-name NAME]")
	}
	cfg, err := client.Pair(ctx, pos[0], pos[1], *name, protocol.DeviceAgent)
	if err != nil {
		return c.fail(1, "pairing failed: %v", err)
	}
	if err := cfg.Save(c.cfgPath); err != nil {
		return c.fail(1, "saving config: %v", err)
	}
	fmt.Fprintf(c.stdout, "paired as %q, config saved to %s\n", *name, c.cfgPath)
	return 0
}

func (c *cli) devices(ctx context.Context) int {
	api, err := c.loadAPI()
	if err != nil {
		return c.fail(1, "%v", err)
	}
	devs, err := api.Devices(ctx)
	if err != nil {
		return c.fail(1, "%v", err)
	}
	tw := tabwriter.NewWriter(c.stdout, 0, 4, 2, ' ', 0)
	fmt.Fprintln(tw, "NAME\tTYPE\tONLINE")
	for _, d := range devs {
		if d.Type == protocol.DeviceAgent {
			continue
		}
		fmt.Fprintf(tw, "%s\t%s\t%v\n", d.Name, d.Type, d.Online)
	}
	tw.Flush()
	return 0
}

func (c *cli) send(ctx context.Context, kind protocol.Kind, args []string) int {
	fs := flag.NewFlagSet("send", flag.ContinueOnError)
	fs.SetOutput(c.stderr)
	to := fs.String("to", "", "comma separated device names")
	all := fs.Bool("all", false, "offer to all devices")
	minutes := fs.Int("minutes", 0, "offer duration in minutes (max 10)")
	message := fs.String("message", "", "optional message")
	pos, err := parseInterleaved(fs, args)
	if err != nil {
		return 2
	}
	if (*to == "") == !*all {
		return c.fail(2, "exactly one of --to or --all is required")
	}
	if len(pos) == 0 || (kind != protocol.KindFiles && len(pos) != 1) {
		return c.fail(2, "nothing to send")
	}
	api, err := c.loadAPI()
	if err != nil {
		return c.fail(1, "%v", err)
	}
	devs, err := api.Devices(ctx)
	if err != nil {
		return c.fail(1, "%v", err)
	}
	var targets []string
	if *to != "" {
		if targets, err = client.ResolveTargets(devs, strings.Split(*to, ",")); err != nil {
			return c.fail(2, "%v", err)
		}
	}
	names := map[string]string{}
	for _, d := range devs {
		names[d.ID] = d.Name
	}
	rctx, stop := context.WithCancel(ctx)
	defer stop()
	sender := client.NewSender(api, client.SenderOptions{})
	go sender.Run(rctx)
	if err := sender.WaitReady(ctx, 10*time.Second); err != nil {
		return c.fail(1, "%v", err)
	}
	var tr protocol.Transfer
	if kind == protocol.KindFiles {
		tr, err = sender.OfferFiles(ctx, pos, targets, *all, *message, *minutes)
	} else {
		tr, err = sender.OfferText(ctx, kind, pos[0], targets, *all)
	}
	if err != nil {
		var ae *client.APIError
		if errors.As(err, &ae) && ae.Status == 400 {
			return c.fail(2, "%v", err)
		}
		return c.fail(1, "%v", err)
	}
	if kind != protocol.KindFiles {
		// the server carries text/links itself; nothing to keep serving
		fmt.Fprintf(c.stdout, "offered (id %s) until %s\n", tr.ID, tr.ExpiresAt.Local().Format("15:04:05"))
		return 0
	}
	fmt.Fprintf(c.stdout, "offered (id %s) until %s – waiting for confirmation\n", tr.ID, tr.ExpiresAt.Local().Format("15:04:05"))
	delivered := false
	for {
		select {
		case <-ctx.Done():
			cctx, cc := context.WithTimeout(context.Background(), 5*time.Second)
			sender.Cancel(cctx, tr.ID)
			cc()
			fmt.Fprintln(c.stdout, "cancelled")
			return 1
		case u := <-sender.Updates():
			if u.TransferID != tr.ID {
				continue
			}
			if u.Closed {
				if delivered {
					return 0
				}
				return 1
			}
			d := u.Delivery
			line := fmt.Sprintf("%s: %s", names[d.DeviceID], d.Status)
			if d.Path != "" && !d.Status.Final() {
				line += fmt.Sprintf(" (%s)", d.Path)
			}
			if total := tr.TotalSize(); total > 0 && d.Status == protocol.StatusDownloading {
				line += fmt.Sprintf(" %d%%", d.Bytes*100/total)
			}
			fmt.Fprintln(c.stdout, line)
			if d.Status == protocol.StatusDelivered {
				delivered = true
			}
		}
	}
}

// mcp serves the MCP tools on stdio. stdout belongs to the protocol: never print to it here.
func (c *cli) mcp(ctx context.Context) int {
	api, err := c.loadAPI()
	if err != nil {
		return c.fail(1, "%v", err)
	}
	ctx, stop := context.WithCancel(ctx)
	defer stop()
	sender := client.NewSender(api, client.SenderOptions{})
	go sender.Run(ctx)
	if err := mcpserver.New(api, sender).Run(ctx, &mcp.StdioTransport{}); err != nil && ctx.Err() == nil {
		return c.fail(1, "mcp: %v", err)
	}
	return 0
}
