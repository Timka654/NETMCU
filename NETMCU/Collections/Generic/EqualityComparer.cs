namespace System.Collections.Generic
{
    public abstract class EqualityComparer<T> // : IEqualityComparer<T> // Ignore interface for now not to depend on missing things
    {
        public static EqualityComparer<T> Default { get; } = null; // Stub

        public abstract bool Equals(T x, T y);
        public abstract int GetHashCode(T obj);
    }
}