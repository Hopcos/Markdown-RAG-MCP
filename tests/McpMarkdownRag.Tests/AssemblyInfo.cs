using Xunit;

// The integration fixture writes process-wide environment variables in its constructor
// (it must run before the host starts); serialize test execution across the assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
