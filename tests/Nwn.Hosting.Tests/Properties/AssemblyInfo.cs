// Many tests in this assembly mutate process-wide state (environment variables that inject fixture
// failures, the console writer the native host logs to, the native host's own statics) and share ambient
// temp-directory fixtures; running test classes in parallel within this assembly would race on that
// shared state. Other test assemblies may still run concurrently with this one.
[assembly: DoNotParallelize]
