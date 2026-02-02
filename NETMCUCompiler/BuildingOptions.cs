namespace NETMCUCompiler
{
    internal class BuildingOptions
    {
        public Dictionary<string, string> replaceable = new()
            {
                { "STARTUP_ADDRESS", "0x08008000" },
                { "CORE_PATH", "" }
            };

        public List<string> include = new();
        public List<string> libraries = new();
        public List<string> packages = new();
        public Dictionary<string, string> defines = new();
        public List<string> drives = new();

    }
}
