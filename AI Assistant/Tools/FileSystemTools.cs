using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
namespace AI_Assistant.Tools
{
    public class FileSystemTools
    {

        private readonly string workspacePath;
       

        public FileSystemTools(string workspacePath) {


            this.workspacePath = workspacePath;
        
        
        }
        public string CreateFolder(string folderName)
        {   
            string fullpath= Path.Combine(workspacePath, folderName);
            Directory.CreateDirectory(fullpath);
            return $"Folder napravljen: {fullpath}";

        }

    }
    
}
