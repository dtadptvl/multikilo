#if NET47_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace EasyWindowsTerminalControl.Internals {
	/// <summary>
	/// Span-based overloads that exist in the BCL on .NET Core 2.1+ but not on .NET Framework, implemented the same way the BCL does (rent from ArrayPool, call the array overload) so call sites compile unchanged across all targets.
	/// </summary>
	internal static class NetFrameworkCompat {
		public static int Read(this TextReader reader, Span<char> buffer) {
			var arr = ArrayPool<char>.Shared.Rent(buffer.Length);
			try {
				var read = reader.Read(arr, 0, buffer.Length);
				if (read > 0)
					new Span<char>(arr, 0, read).CopyTo(buffer);
				return read;
			} finally {
				ArrayPool<char>.Shared.Return(arr);
			}
		}
		public static void Write(this TextWriter writer, ReadOnlySpan<char> buffer) {
			var arr = ArrayPool<char>.Shared.Rent(buffer.Length);
			try {
				buffer.CopyTo(arr);
				writer.Write(arr, 0, buffer.Length);
			} finally {
				ArrayPool<char>.Shared.Return(arr);
			}
		}
		public static void Write(this BinaryWriter writer, ReadOnlySpan<byte> buffer) {
			var arr = ArrayPool<byte>.Shared.Rent(buffer.Length);
			try {
				buffer.CopyTo(arr);
				writer.Write(arr, 0, buffer.Length);
			} finally {
				ArrayPool<byte>.Shared.Return(arr);
			}
		}
		public static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes) {
			var arr = ArrayPool<byte>.Shared.Rent(bytes.Length);
			try {
				bytes.CopyTo(arr);
				return encoding.GetString(arr, 0, bytes.Length);
			} finally {
				ArrayPool<byte>.Shared.Return(arr);
			}
		}
	}
}
#endif
