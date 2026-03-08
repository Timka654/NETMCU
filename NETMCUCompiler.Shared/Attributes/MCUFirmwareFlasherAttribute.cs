namespace NETMCUCompiler.Shared.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class MCUFirmwareFlasherAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}
