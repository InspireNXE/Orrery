using System.Text;
using Orrery.Core.Network;
using Orrery.Core.Sessions;

namespace Orrery.Screenshots;

/// <summary>A terminal session that only shows what it's given: no shell, no network.</summary>
sealed class DemoTerminal(string host, SessionState state) : ITerminalSession
{
    public ConnectionTarget Target { get; } = new(host, 22, "demo", null);
    public SessionState State { get; private set; } = state;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event Action<byte[]>? DataReceived;

    public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));

    public void Fail(SessionFailureException error)
    {
        State = SessionState.Failed;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Failed, error.Message, error));
    }

    public void Send(ReadOnlySpan<byte> data) { }
    public void Resize(int columns, int rows) { }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A host check that answers from a list instead of the network.</summary>
sealed class DemoProbe(IReadOnlySet<string> down) : IHostProbe
{
    public HostCheckMethod Method => HostCheckMethod.Tcp;
    public string KeyFor(string host, int port) => $"{host}:{port}";
    public Task<ProbeAnswer> ProbeAsync(string host, int port, CancellationToken cancellationToken) =>
        Task.FromResult(down.Contains(host) ? ProbeAnswer.Down : ProbeAnswer.Up);
}

/// <summary>What the web-01 tab shows: a login and a few commands, with the colors a stock Ubuntu shell uses.</summary>
static class DemoShell
{
    const string Green = "\e[1;32m", Blue = "\e[1;34m", Cyan = "\e[1;36m", Yellow = "\e[33m", Reset = "\e[0m";
    static readonly string Prompt = $"{Green}demo@web-01{Reset}:{Blue}~{Reset}$ ";

    public static readonly string Text = $"""
        Welcome to Ubuntu 24.04.1 LTS (GNU/Linux 6.8.0-48-generic x86_64)

          System load:  0.08               Processes:             141
          Usage of /:   41.6% of 48.02GB   Users logged in:       1
          Memory usage: 23%                IPv4 address for eth0: 10.20.0.11

        Last login: Sat Oct 31 09:12:44 2026 from 10.20.8.15
        {Prompt}systemctl status nginx --no-pager
        {Green}●{Reset} nginx.service - A high performance web server
             Loaded: loaded (/usr/lib/systemd/system/nginx.service; enabled)
             Active: {Green}active (running){Reset} since Thu 2026-10-29; 2 days ago
           Main PID: 1187 (nginx)
              Tasks: 5 (limit: 4553)
             Memory: 9.8M (peak: 12.1M)
        {Prompt}df -h /srv
        Filesystem      Size  Used Avail Use% Mounted on
        /dev/sdb1        98G   37G   57G  40% /srv
        {Prompt}ls /srv/www
        {Cyan}current{Reset}  deploy.log  {Blue}releases{Reset}  {Blue}shared{Reset}
        {Prompt}git -C /srv/www/current log --oneline -3
        {Yellow}a41c9e2{Reset} ({Cyan}HEAD -> {Green}main{Reset}, {Yellow}tag: v2.4.1{Reset}) Cache static assets for a day
        {Yellow}7d02b18{Reset} Add a health check endpoint
        {Yellow}3f9e6c4{Reset} Move uploads to /srv/shared
        {Prompt}uptime
         10:42:15 up 2 days,  3:01,  1 user,  load average: 0.08, 0.05, 0.01
        {Prompt}
        """;
}
