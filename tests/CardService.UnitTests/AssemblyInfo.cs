// The endpoint tests each boot their own WebApplicationFactory<Program> host. Several hosts
// started in parallel collide on the process-wide host resolver used for top-level-statement
// Program entry points, which intermittently fails with "The entry point exited without ever
// building an IHost". Running this assembly's tests sequentially makes the suite deterministic
// (the trade-off is a slightly slower run, acceptable for this small suite).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
