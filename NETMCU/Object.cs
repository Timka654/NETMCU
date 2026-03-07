
namespace System
{
    public class Object
    {
        public Object() { }

        public virtual bool Equals(object obj)
        {
            return ReferenceEquals(this, obj);
        }

        public static bool ReferenceEquals(object objA, object objB)
        {
            return objA == objB;
        }

        public virtual int GetHashCode()
        {
            // Placeholder: Should return address or intrinsic instance ID
            return 0;
        }

        public virtual string ToString()
        {
            return "System.Object";
        }
    }
}
