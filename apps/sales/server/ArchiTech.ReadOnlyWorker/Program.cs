using ArchiTech.ReadOnlyWorker;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
return await WorkerCommand.RunAsync(args, Console.In, Console.Out, Console.Error, cancellation.Token);
