using System.Collections.Generic;

namespace WoWRegeneration.Data
{
    public class FileObject
    {
        public string Url { get; set; }


        public string Path { get; set; }
        public string Info { get; set; }

        /// <summary>The file's tag= values (version 3 manifests).</summary>
        public List<string> Tags { get; set; } = new List<string>();

        public string Directory
        {
            get { return Path.Replace(Filename, ""); }
        }

        public string Filename
        {
            get { return System.IO.Path.GetFileName(Path); }
        }

        public override string ToString()
        {
            return Path;
        }
    }
}