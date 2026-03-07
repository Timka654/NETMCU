
using System.MCU.Compiler.Attributes;

namespace System
{

    [CompilerType]
    public class Attribute
    {

    }


    [CompilerType]
    public class ComVisibleAttribute(bool v) : Attribute { 
    
    }


    [CompilerType]
    public class SerializableAttribute : Attribute { 
    
    }
}
