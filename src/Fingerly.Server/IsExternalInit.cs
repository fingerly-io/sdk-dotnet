#if NETSTANDARD2_0
using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Lets <c>init</c> accessors compile for .NET Standard 2.0, whose runtime
/// predates the marker the C# compiler emits for them. The models are
/// immutable once deserialized on every target because of it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif
