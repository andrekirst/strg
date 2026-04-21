// Top-level-statements Program has an internal generated entry-point class. Exposing it as a
// partial public class lets WebApplicationFactory<Program> in the integration test project
// target the real host. No behavior change — this file adds only the visibility shim.
public partial class Program;
