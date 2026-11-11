// Each test is isolated (its own mock on its own port and its own GameService host), so the tests
// are logically independent. But parallel WebApplicationFactory<Program> starts collide on the
// process-wide host resolver used for top-level-statement Program entry points (same flakiness seen
// in the unit-test assemblies), so this assembly runs sequentially for determinism. Concurrency
// under load is instead exercised by running this assembly alongside another test run (a separate
// process), which stresses the timing margins without the in-process host-start collision.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
