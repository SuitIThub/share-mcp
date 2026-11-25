using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace Share.App.Tests;

public sealed class IpcTests : IDisposable
{
    private readonly string _name = "share-test-" + Guid.NewGuid();
    private readonly List<Ipc> _all = [];
    private readonly ConcurrentQueue<Command> _got = new();

    public void Dispose() => _all.ForEach(i => i.Dispose());

    private Ipc New()
    {
        var i = new Ipc(_name);
        _all.Add(i);
        return i;
    }

    private Ipc First()
    {
        var i = New();
        Assert.True(i.IsFirst);
        i.Listen(c =>
        {
            _got.Enqueue(c);
            return Task.CompletedTask;
        });
        return i;
    }

    [Fact]
    public void SecondInstanceForwards()
    {
        First();
        var second = New();
        Assert.False(second.IsFirst);
        Command[] all = [new Command.Show(), new Command.Send(["C:\\ä ö.txt", "D:\\b"], true), new Command.SendText("häl\nlo \"x\""), new Command.Pair("share://pair?code=A&url=b")];
        foreach (var c in all) Assert.True(second.TrySend(c));
        Assert.Equal(4, _got.Count);
        Assert.Equal(all[0], _got.ElementAt(0));
        var send = Assert.IsType<Command.Send>(_got.ElementAt(1));
        Assert.Equal(["C:\\ä ö.txt", "D:\\b"], send.Paths);
        Assert.True(send.Temporary);
        Assert.Equal(all[2], _got.ElementAt(2));
        Assert.Equal(all[3], _got.ElementAt(3));
    }

    [Fact]
    public void AnswerComesOnlyAfterTheCommandIsHandled()
    {
        var done = false;
        var first = New();
        first.Listen(async _ =>
        {
            await Task.Delay(500);
            done = true;
        });
        Assert.True(New().TrySend(new Command.Show()));
        Assert.True(done);
    }

    [Fact]
    public void FailingHandlerIsReportedAndTheNextOneStillWorks()
    {
        var first = New();
        first.Listen(c => c is Command.Show ? throw new InvalidOperationException("no") : Task.CompletedTask);
        var second = New();
        Assert.False(second.TrySend(new Command.Show()));
        Assert.True(second.TrySend(new Command.SendText("x")));
    }

    [Fact]
    public async Task ParallelStartsYieldOneInstance()
    {
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => new Ipc(_name))));
        _all.AddRange(all);
        Assert.Single(all, i => i.IsFirst);
    }

    [Fact]
    public async Task ManySendersAtOnce()
    {
        First();
        // each sender is a process of its own in reality, so each gets a thread here
        var senders = Enumerable.Range(0, 20).Select(_ => New()).ToList();
        var ok = await Task.WhenAll(senders.Select((s, i) => Task.Factory.StartNew(() => s.TrySend(new Command.SendText("t" + i)), TaskCreationOptions.LongRunning)));
        Assert.All(ok, Assert.True);
        Assert.Equal(20, _got.Count);
    }

    [Fact]
    public void AfterTheFirstIsGoneAnotherCanBeFirst()
    {
        var a = First();
        a.Dispose();
        Assert.True(New().IsFirst);
    }

    private void Raw(byte[] bytes)
    {
        using var p = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        p.Connect(2000);
        try
        {
            p.Write(bytes);
            p.Flush();
            p.ReadByte();
        }
        catch (IOException)
        {
            // refused
        }
    }

    private static byte[] Frame(string json)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(json);
        return [.. BitConverter.GetBytes(b.Length), .. b];
    }

    [Fact]
    public void GarbageOnThePipeIsIgnored()
    {
        First();
        Raw([]);
        Raw([1]);
        Raw([255, 255, 255, 255]);
        Raw([1, 0, 0, 0, 0]);
        foreach (var json in new[] { "kaputt", "{}", "null", "[]", """{"cmd":"neu"}""", """{"cmd":"send"}""", """{"cmd":"send","paths":[1]}""", """{"cmd":"text","text":5}""" }) Raw(Frame(json));
        Assert.Empty(_got);
        Assert.True(New().TrySend(new Command.Show()));
        Assert.Single(_got);
    }

    /// <summary>Answering offers must stay with the user's click.</summary>
    [Fact]
    public void AnswersAreNotAcceptedOverThePipe()
    {
        First();
        foreach (var json in new[] { """{"cmd":"accept","id":"x"}""", """{"cmd":"decline","id":"x"}""" }) Raw(Frame(json));
        Assert.False(New().TrySend(new Command.Accept("x")));
        Assert.False(New().TrySend(new Command.Decline("x")));
        Assert.Empty(_got);
    }

    [Fact]
    public void HugeMessageIsRefused()
    {
        First();
        Raw(BitConverter.GetBytes(50_000_000));
        Assert.False(New().TrySend(new Command.SendText(new string('a', 2_000_000))));
        Assert.Empty(_got);
        Assert.True(New().TrySend(new Command.Show()));
    }

    [Fact]
    public void HungFirstInstanceDoesNotBlockForever()
    {
        var first = New(); // never listens
        Assert.True(first.IsFirst);
        var sw = Stopwatch.StartNew();
        Assert.False(New().TrySend(new Command.Show(), TimeSpan.FromSeconds(2)));
        Assert.True(sw.Elapsed.TotalSeconds < 5);
        first.Listen(_ => Task.Delay(Timeout.Infinite));
        sw.Restart();
        Assert.False(New().TrySend(new Command.Show(), TimeSpan.FromSeconds(2)));
        Assert.True(sw.Elapsed.TotalSeconds < 5);
    }
}
