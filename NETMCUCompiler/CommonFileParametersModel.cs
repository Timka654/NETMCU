namespace NETMCUCompiler
{
    public class CommonFileParametersModel
    {

        public string[] ProcessingPathes { get; set; } = Array.Empty<string>();
        public string[] RequiredValues { get; set; } = Array.Empty<string>();

        public Dictionary<string, string> DefaultValues { get; set; } = new();
    }
    //public record LinkerRecord(MethodCompilationContext context, int position, bool isStatic);

    //public class LinkerContext
    //{
    //    public Dictionary<string, LinkerRecord> OutputMethods { get; } = new();

    //    public Dictionary<string, TypeMetadata> OutputTypes { get; } = new();

    //    public Dictionary<string, List<RelocationRecord>> InputMethods { get; } = new();

    //    public Dictionary<string, object> Constants { get; } = new();
    //}
}
