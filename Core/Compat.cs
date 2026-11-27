#if NETFRAMEWORK
// .NET Framework 4.8 (Rhino 7) lacks a few APIs this code uses from .NET 7 (Rhino 8 and 9).
// These stand-ins keep one code base for both builds.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace Go2Bed
{
    internal static class Compat
    {
        internal static IEnumerable<T> DistinctBy<T, K>(this IEnumerable<T> items, Func<T, K> key)
        {
            var seen = new HashSet<K>();
            foreach (var item in items) if (seen.Add(key(item))) yield return item;
        }

        internal static V GetValueOrDefault<K, V>(this Dictionary<K, V> map, K key, V fallback = default!)
            => map.TryGetValue(key, out var value) ? value : fallback;

        internal static bool Contains(this string text, string value, StringComparison comparison)
            => text.IndexOf(value, comparison) >= 0;

        internal static T MaxBy<T, K>(this IEnumerable<T> items, Func<T, K> key) where K : IComparable<K>
            => items.Aggregate((best, item) => key(item).CompareTo(key(best)) > 0 ? item : best);

        internal static T MinBy<T, K>(this IEnumerable<T> items, Func<T, K> key) where K : IComparable<K>
            => items.Aggregate((best, item) => key(item).CompareTo(key(best)) < 0 ? item : best);

        // Rhino 7's Group has no IsDeleted, so every group an object still lists counts.
        internal static bool IsDeleted(Rhino.DocObjects.Group group) => false;
    }
}
#endif

#if !NETFRAMEWORK
namespace Go2Bed
{
    internal static class Compat
    {
        internal static bool IsDeleted(Rhino.DocObjects.Group group) => group.IsDeleted;
    }
}
#endif
