// Several endpoint tests each boot their own WebApplicationFactory<Program> host. Parallel hosts
// collide on the process-wide host resolver used for top-level-statement Program entry points,
// which intermittently fails with "The entry point exited without ever building an IHost". Running
// this assembly's tests sequentially keeps the suite deterministic (same reasoning as del 1).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
