using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Lantern.Core
{
    /// <summary>
    /// Checks the Authenticode signature of a program before Lantern runs it. Lantern's own copy of the Roblox client is downloaded
    /// and then started, so "the bytes match the checksum in Roblox's package list" (which comes from the same server) is backed by
    /// "and Windows agrees the file is signed by Roblox and unchanged since".
    /// </summary>
    public static class CodeSignature
    {
        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, StateVerify = 1, StateClose = 2;
        const uint RevocationCheckNone = 0x10;   // no network round trip for a revocation list: the file is checked, not the internet

        /// <summary>
        /// True when <paramref name="path"/> carries a valid embedded signature (the file is unchanged since it was signed and the
        /// certificate chains to a root Windows trusts) from <paramref name="publisher"/>. <paramref name="problem"/> says why not.
        /// </summary>
        public static bool IsSignedBy(string path, string publisher, out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(path)) { problem = "the file is missing"; return false; }

                int result = Verify(path);
                if (result != 0)
                {
                    problem = result == unchecked((int)0x800B0100) ? "it has no signature"
                            : result == unchecked((int)0x80096010) ? "its contents don't match its signature"
                            : "Windows doesn't trust its signature (0x" + result.ToString("X8") + ")";
                    return false;
                }

                string signer;
                using (var signed = X509Certificate.CreateFromSignedFile(path))
                using (var cert = new X509Certificate2(signed))
                    signer = cert.GetNameInfo(X509NameType.SimpleName, false);
                if (!string.Equals(signer, publisher, StringComparison.OrdinalIgnoreCase))
                {
                    problem = "it is signed by \"" + signer + "\", not \"" + publisher + "\"";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static int Verify(string path)
        {
            var file = new WinTrustFileInfo { pcwszFilePath = path };
            file.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var action = GenericVerifyV2;
            var data = new WinTrustData
            {
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = pFile,
                dwStateAction = StateVerify,
                dwProvFlags = RevocationCheckNone
            };
            data.cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = StateClose;   // frees what the verify call kept
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
