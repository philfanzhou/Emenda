// Host factories hand their startup configuration to Program through process-wide
// environment variables (Program reads configuration before WebApplicationFactory
// overrides apply). Parallel test classes would overwrite each other's caller
// credentials and connections, so the assembly runs its tests sequentially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
