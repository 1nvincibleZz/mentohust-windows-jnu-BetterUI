using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MentoHUST.Desktop
{
    public static class LegacyCodec
    {
        // Matches MentoHUST.cpp EncodeRuijie / DecodeRuijie; not a new credential format.
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("~!:?$*<(qw2e5o7i8x12c6m67s98w43d2l45we82q3iuu1z4xle23rt4oxclle34e54u6r8m");
        [DllImport("kernel32.dll")] private static extern uint GetACP();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WideCharToMultiByte(uint codePage, uint flags, string text, int length,
            byte[] bytes, int capacity, IntPtr defaultChar, out bool usedDefault);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int length,
            [Out] char[] text, int capacity);

        internal static byte[] ToAnsi(string text)
        {
            if (text.Length == 0) return new byte[0];
            if (GetACP() == 65001) return new UTF8Encoding(false, true).GetBytes(text);
            bool replacement;
            int size = WideCharToMultiByte(0, 0x400, text, text.Length, null, 0, IntPtr.Zero, out replacement);
            var result = new byte[size];
            if (size == 0 || WideCharToMultiByte(0, 0x400, text, text.Length, result, size, IntPtr.Zero, out replacement) == 0 || replacement)
                throw new ArgumentException("密码中包含旧客户端无法保存的字符。");
            return result;
        }

        internal static string FromAnsi(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            int length = MultiByteToWideChar(0, 8, bytes, bytes.Length, null, 0);
            if (length == 0) throw new ArgumentException("旧配置的文字编码无效。");
            var text = new char[length];
            if (MultiByteToWideChar(0, 8, bytes, bytes.Length, text, length) == 0)
                throw new ArgumentException("旧配置的文字编码无效。");
            return new string(text);
        }

        public static string Encode(string password)
        {
            if (password.IndexOf('\0') >= 0) throw new ArgumentException("密码不能包含空字符。");
            byte[] bytes = ToAnsi(password);
            if (bytes.Length > Key.Length) throw new ArgumentException("密码超过旧配置格式允许的长度。");
            for (int i = 0; i < bytes.Length; i++) bytes[i] ^= Key[i];
            try { return Convert.ToBase64String(bytes); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        public static bool TryDecode(string encoded, out string password)
        {
            password = ""; byte[] bytes = null;
            try {
                // The legacy parser does not allow whitespace inside Base64.
                if (encoded.Length % 4 != 0 || encoded.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0) return false;
                bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > Key.Length) return false;
                for (int i = 0; i < bytes.Length; i++) bytes[i] ^= Key[i];
                if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
                password = FromAnsi(bytes); return true;
            } catch (FormatException) { return false; }
              catch (ArgumentException) { return false; }
            finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        }
    }
}
