// Command share-server runs the share broker and offers admin commands.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"text/tabwriter"
	"time"

	"github.com/mdp/qrterminal/v3"

	"share/internal/server"
	"share/internal/store"
)

const usage = `usage: share-server [-config path] <command>

commands:
  serve               run the server
  pair                create a pairing code (shows QR code)
  devices ls          list paired devices
  devices rm <name>   remove a device
`

func main() {
	fs := flag.NewFlagSet("share-server", flag.ExitOnError)
	cfgPath := fs.String("config", "/etc/share-server/config.toml", "config file")
	fs.Usage = func() { fmt.Fprint(os.Stderr, usage) }
	fs.Parse(os.Args[1:])
	args := fs.Args()
	if len(args) == 0 {
		fs.Usage()
		os.Exit(2)
	}
	cfg, err := loadConfig(*cfgPath)
	if err != nil {
		log.Fatalf("config: %v", err)
	}
	st, err := store.Open(cfg.DBPath)
	if err != nil {
		log.Fatalf("database: %v", err)
	}
	defer st.Close()
	switch args[0] {
	case "serve":
		serve(cfg, st)
	case "pair":
		code, err := st.CreatePairCode(time.Now())
		if err != nil {
			log.Fatal(err)
		}
		if cfg.PublicURL != "" {
			qrterminal.GenerateHalfBlock(pairURI(cfg.PublicURL, code), qrterminal.L, os.Stdout)
			fmt.Printf("Server: %s\n", cfg.PublicURL)
		}
		fmt.Printf("Code:   %s  (10 Minuten gültig)\n", code)
	case "devices":
		if len(args) >= 2 && args[1] == "ls" {
			devs, err := st.Devices()
			if err != nil {
				log.Fatal(err)
			}
			tw := tabwriter.NewWriter(os.Stdout, 0, 4, 2, ' ', 0)
			fmt.Fprintln(tw, "NAME\tTYP\tZULETZT GESEHEN")
			for _, d := range devs {
				fmt.Fprintf(tw, "%s\t%s\t%s\n", d.Name, d.Type, d.LastSeen.Local().Format("2006-01-02 15:04"))
			}
			tw.Flush()
		} else if len(args) == 3 && args[1] == "rm" {
			d, err := st.DeviceByName(args[2])
			if err != nil {
				log.Fatalf("device %q: %v", args[2], err)
			}
			if err := st.DeleteDevice(d.ID); err != nil {
				log.Fatal(err)
			}
			fmt.Printf("%s entfernt\n", d.Name)
		} else {
			fs.Usage()
			os.Exit(2)
		}
	default:
		fs.Usage()
		os.Exit(2)
	}
}

func serve(cfg fileConfig, st *store.Store) {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	var notifier server.Notifier
	if cfg.FirebaseCredentials != "" {
		n, err := server.NewFCMNotifier(ctx, cfg.FirebaseCredentials)
		if err != nil {
			log.Fatalf("firebase: %v", err)
		}
		notifier = n
		log.Printf("firebase push enabled")
	} else {
		log.Printf("firebase not configured: Android only sees offers while the app is open")
	}
	srv := server.New(st, server.Config{MaxOfferMinutes: cfg.MaxOfferMinutes, Notifier: notifier})
	go srv.Run(ctx)
	hs := &http.Server{Addr: cfg.Listen, Handler: srv.Handler(), ReadHeaderTimeout: 10 * time.Second}
	go func() {
		<-ctx.Done()
		srv.Close()
		sctx, c := context.WithTimeout(context.Background(), 5*time.Second)
		defer c()
		hs.Shutdown(sctx)
	}()
	log.Printf("listening on %s", cfg.Listen)
	if err := hs.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatal(err)
	}
}
