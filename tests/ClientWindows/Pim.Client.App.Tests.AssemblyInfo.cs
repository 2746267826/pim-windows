using Xunit;

// BootstrapLogTests temporarily rewrites process-wide static paths. Other WPF tests
// can write BootstrapLog concurrently, so this small test assembly runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
