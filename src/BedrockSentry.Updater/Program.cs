using BedrockSentry.Updater;

// BedrockSentry.Updater — swaps the wrapper's files while the service is stopped,
// since a running service cannot overwrite its own loaded assemblies.
//
// Exit codes are meaningful. The service reads the result file, but an operator
// running this by hand gets these:
//   0  update applied and verified healthy
//   1  update failed and the previous build was restored
//   2  update failed AND the rollback failed — the service may be down
//   3  the plan file was missing or unreadable
//   4  refused to run: the helper is inside the tree it would replace

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BedrockSentry.Updater <plan.json>");
    return 3;
}

return await UpdateRunner.RunAsync(args[0]);
