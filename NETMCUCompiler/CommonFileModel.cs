namespace NETMCUCompiler
{
    public class CommonFileModel
    {
        public string Name { get; set; }
        public string ImageName { get; set; }
        public string State { get; set; }
        public DateTime PublishDate { get; set; }

        public CommonFileParametersModel Parameters { get; set; } = new();
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
