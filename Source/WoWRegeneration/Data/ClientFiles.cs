using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WoWRegeneration.Repositories;

namespace WoWRegeneration.Data
{
    /// <summary>
    ///     Builds the Windows client files (Wow.exe, Wow-64.exe and their helpers) that the MPQ
    ///     manifest does not list as loose files, and writes WoW.mfil, so the download folder is a
    ///     playable 5.4.8 client.
    ///
    ///     Everything comes from two MPQs the manifest already downloads: Data/base-Win.MPQ holds
    ///     the 5.0.3 (15890) installation files, and Updates/wow-0-18414-Win-final.MPQ holds the
    ///     newer files plus binary patches that turn the 15890 Wow.exe, Wow-64.exe and
    ///     MovieProxy.exe into their 18414 versions. Every result is checked against its known MD5.
    /// </summary>
    public static class ClientFiles
    {
        private const string BaseArchive = "Data/base-Win.MPQ";
        private const string UpdateArchive = "Updates/wow-0-18414-Win-final.MPQ";
        private const string UpdateFolder = "pc-game-hdfiles\\";

        private const string MfilName = "WoW.mfil";

        /// <summary>
        ///     Client file (MPQ path, relative to the client folder) to the MD5 of its 18414 version.
        ///     Taken from the update archive's patch.lst and hdfiles.lst; Battle.net.dll, msvcr80.dll
        ///     and WowError.exe are left out because the update's delete.lst removes them.
        /// </summary>
        private static readonly Dictionary<string, string> Files = new Dictionary<string, string>
            {
                {"Wow.exe", "24FD2CBB340D57C51B6F7A1C1D60E693"},
                {"Wow-64.exe", "96EF9239F97336F453562D350C33BCC7"},
                {"MovieProxy.exe", "988F5E3A0FEB790862F69DE423F0E67A"},
                {"BlizzardError.exe", "98CB5B27549A3C9DD5CBC4F58F5A5BDB"},
                {"SystemSurvey.exe", "5E9E75B78AF45FE376022B4D61767109"},
                {"dbghelp.dll", "4003E34416EBD25E4C115D49DC15E1A7"},
                {"divxdecoder.dll", "57E72CAE12091DAFA29A8E4DB8B4F1D1"},
                {"Utils\\icudt.dll", "5434E18B933E03F274D8DA59FDA4C676"},
                {"Utils\\libcef.dll", "EC729CC8FEADAB8C4C2108D83E8BFE27"},
                {"Utils\\WowBrowserProxy.exe", "34D23F2255A984429829F50CC92F50DB"},
                {"Utils\\locales\\de.pak", "322F0CFE2ABF63720FB670AFF881B3B8"},
                {"Utils\\locales\\en-US.pak", "783EBA49FCDDC18A0DB34D5E25929123"},
                {"Utils\\locales\\es-419.pak", "63E6F0B16EC41CE77F25C22DD76FBC52"},
                {"Utils\\locales\\es.pak", "39A226FF222F39585D539A428EC1B1CE"},
                {"Utils\\locales\\fr.pak", "AC48F40AD0D733C1B9C968470788FD3A"},
                {"Utils\\locales\\it.pak", "D3D4FE48FC2C369AAE35A1727493F72E"},
                {"Utils\\locales\\ko.pak", "2913D096EFFC4856425AB23DEC049529"},
                {"Utils\\locales\\pt-BR.pak", "DD74C77BBED85B8D6F08586747A38148"},
                {"Utils\\locales\\ru.pak", "CACF6338BA33BD49C399C02267B7F23C"},
                {"Utils\\locales\\zh-CN.pak", "CA54E31D12F8B301C2F57212FDDCC2DE"},
                {"Utils\\locales\\zh-TW.pak", "E7F5CCACB045AE070FD982A2EF4A0179"}
            };

        /// <summary>
        ///     Makes sure every client file is present in basePath with the right MD5. Files that
        ///     are already correct are skipped; a file with the wrong MD5 is kept as name_BACKUP
        ///     and rebuilt. Returns false if a file could not be built or verified.
        /// </summary>
        public static bool Build(WoWRepository repository, string basePath)
        {
            Program.Log();
            Program.Log("Checking client files (Wow.exe and libraries)", ConsoleColor.Yellow);

            MpqArchive baseArchive = null;
            MpqArchive updateArchive = null;
            try
            {
                foreach (KeyValuePair<string, string> file in Files)
                {
                    string path = Path.Combine(basePath, file.Key.Replace('\\', Path.DirectorySeparatorChar));
                    if (IsPresent(path, file.Value))
                        continue;

                    if (baseArchive == null)
                    {
                        baseArchive = OpenArchive(basePath, BaseArchive);
                        updateArchive = OpenArchive(basePath, UpdateArchive);
                        if (baseArchive == null || updateArchive == null)
                            return false;
                    }

                    Program.Log("Extracting " + file.Key, ConsoleColor.Yellow);
                    byte[] data = updateArchive.ReadFile(UpdateFolder + file.Key) ?? baseArchive.ReadFile(file.Key);
                    if (data == null)
                    {
                        Program.Log(file.Key + " is in neither " + BaseArchive + " nor " + UpdateArchive, ConsoleColor.Red);
                        return false;
                    }

                    if (BlizzardPatch.IsPatch(data))
                    {
                        byte[] original = baseArchive.ReadFile(file.Key);
                        if (original == null)
                        {
                            Program.Log("The 5.0.3 " + file.Key + " to patch is missing from " + BaseArchive, ConsoleColor.Red);
                            return false;
                        }
                        Program.Log("Patching " + file.Key + " to build " + repository.GetBuild(), ConsoleColor.Yellow);
                        data = BlizzardPatch.Apply(data, original);
                    }

                    if (Md5Of(data) != file.Value)
                    {
                        Program.Log(file.Key + " has MD5 " + Md5Of(data) + ", expected " + file.Value, ConsoleColor.Red);
                        return false;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, data);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is NotSupportedException || ex is IOException)
            {
                Program.Log("Could not build the client files: " + ex.Message, ConsoleColor.Red);
                return false;
            }
            finally
            {
                baseArchive?.Dispose();
                updateArchive?.Dispose();
            }

            WriteMfil(repository, basePath);

            Program.Log("Client files are complete", ConsoleColor.Green);
            return true;
        }

        private static MpqArchive OpenArchive(string basePath, string name)
        {
            string path = Path.Combine(basePath, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                Program.Log(name + " is missing, so the client files cannot be built. Run the program again to download it.",
                            ConsoleColor.Red);
                return null;
            }
            return new MpqArchive(path);
        }

        /// <summary>
        ///     WoW.mfil points the client's updater at the manifest. The client rewrites it unless
        ///     it is read-only.
        /// </summary>
        private static void WriteMfil(WoWRepository repository, string basePath)
        {
            string content =
                "version=2\r\n" +
                "server=akamai\r\n" +
                "\tlocation=" + repository.GetBaseUrl() + "\r\n" +
                "manifest_partial=" + repository.GetMFilName() + "\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(content);
            string md5 = Md5Of(bytes);

            string path = Path.Combine(basePath, MfilName);
            if (!IsPresent(path, md5))
            {
                Program.Log("Writing " + MfilName, ConsoleColor.Yellow);
                File.WriteAllBytes(path, bytes);
            }
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        /// <summary>
        ///     True if path exists with the expected MD5. A file with a different MD5 is renamed
        ///     to name_BACKUP.ext (replacing an older backup) so it can be built again.
        /// </summary>
        private static bool IsPresent(string path, string md5)
        {
            if (!File.Exists(path))
                return false;

            if (Md5Of(File.ReadAllBytes(path)) == md5)
            {
                Program.Log(Path.GetFileName(path) + " is already present, skipping", ConsoleColor.DarkGray);
                return true;
            }

            string backup = Path.Combine(Path.GetDirectoryName(path),
                                         Path.GetFileNameWithoutExtension(path) + "_BACKUP" + Path.GetExtension(path));
            File.SetAttributes(path, FileAttributes.Normal);
            File.Move(path, backup, true);

            Program.Log(Path.GetFileName(path) + " does not match its known MD5; kept it as " +
                        Path.GetFileName(backup), ConsoleColor.DarkYellow);
            return false;
        }

        private static string Md5Of(byte[] data)
        {
            return Convert.ToHexString(MD5.HashData(data));
        }
    }
}
