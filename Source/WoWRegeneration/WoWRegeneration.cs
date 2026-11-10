using System;
using System.Collections.Generic;
using System.IO;
using WoWRegeneration.Data;
using WoWRegeneration.Repositories;
using WoWRegeneration.UI;

namespace WoWRegeneration
{
    public static class WoWRegeneration
    {
        public static Session CurrentSession { get; set; }

        public static void Process()
        {
            Session previousSession = Session.LoadSession();

            if (previousSession == null)
            {
                EntryPointNewSession();
            }
            else if (previousSession.SessionCompleted)
            {
                EntryPointNewSession();
            }
            else if (RepositoriesManager.GetRepositoryByMfil(previousSession.MFil) == null)
            {
                // A session from another build (for example the 4.3.4 version of this tool).
                Program.Log("Ignoring an unfinished session for " + previousSession.WoWRepositoryName +
                            ", which this version cannot download.", ConsoleColor.DarkYellow);
                Program.Log();
                EntryPointNewSession();
            }
            else
            {
                if (!UserInputs.SelectContinueSession(previousSession))
                    EntryPointNewSession();
                else
                    EntryPointResumeSession(previousSession);
            }
        }

        private static void EntryPointNewSession()
        {
            WoWRepository repository = UserInputs.SelectRepository();
            ManifestFile manifest = ManifestFile.FromRepository(repository);
            string locale = UserInputs.SelectLocale(manifest);
            string os = UserInputs.SelectOs();

            CurrentSession = new Session(repository.GetMFilName(), locale, os);
            CurrentSession.SaveSession();

            StartProcess(manifest);
        }

        private static void EntryPointResumeSession(Session previousSession)
        {
            CurrentSession = previousSession;

            WoWRepository repository = RepositoriesManager.GetRepositoryByMfil(CurrentSession.MFil);
            ManifestFile manifest = ManifestFile.FromRepository(repository);

            CurrentSession.SaveSession();

            StartProcess(manifest);
        }

        private static void StartProcess(ManifestFile manifest)
        {
            Program.Log("Generating file list");
            WoWRepository repository = RepositoriesManager.GetRepositoryByMfil(CurrentSession.MFil);
            List<FileObject> files = manifest.GenerateFileList();

            string aria2 = Aria2.Resolve();
            if (aria2 == null)
                return;

            var downloader = new FileDownloader(repository, files);
            if (!downloader.Start(aria2))
                return;

            // The client executables are Windows files; the MPQ data is shared by both systems.
            if (CurrentSession.Os == "Win")
            {
                if (!ClientFiles.Build(repository, Path.GetFullPath(repository.GetDefaultDirectory())))
                {
                    Program.Log("Client files are incomplete. Run the program again to retry.", ConsoleColor.Red);
                    return;
                }
            }
            else
            {
                Program.Log("Skipping the Windows client files (Wow.exe etc.) for OS " + CurrentSession.Os,
                            ConsoleColor.DarkGray);
            }

            CurrentSession.SessionCompleted = true;
            CurrentSession.SaveSession();
            CurrentSession.Destroy();
            Program.Log("Download Complete !!", ConsoleColor.Green);
        }
    }
}