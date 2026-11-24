using Ino.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await Commands.RunAsync(args, CliContext.FromProcess(), cts.Token);
