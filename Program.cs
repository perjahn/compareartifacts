using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace compareartifacts
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var newFiles = GetLocalArtifacts();
            Log($"Comparing {newFiles.Length} new files.");

            var previousFiles = await DownloadPreviousArtifacts();
            Log($"Comparing {previousFiles.Length} previuous files.");

            var result = CompareFiles(previousFiles, newFiles, true);

            return result ? 0 : 1;
        }

        static bool CompareFiles(string[] previousFiles, string[] newFiles, bool verboseLogging)
        {
            if (previousFiles.Length != newFiles.Length)
            {
                Log($"File count diff: previous={previousFiles.Length}, new={newFiles.Length}");
                return false;
            }
            if (previousFiles.Length == 0 && newFiles.Length == 0)
            {
                Log($"File count diff: previous={previousFiles.Length}, new={newFiles.Length}");
                return true;
            }

            Array.Sort(previousFiles);
            Array.Sort(newFiles);

            bool diff = false;

            int rootFolderOffset1 = previousFiles[0].IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) + 1;
            int rootFolderOffset2 = newFiles[0].IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) + 1;

            for (int i = 0; i < previousFiles.Length; i++)
            {
                string file1 = previousFiles[i];
                string file2 = newFiles[i];

                Log($"Comparing: '{file1}' '{file2}'");
                string f1 = file1.Substring(rootFolderOffset1);
                string f2 = file2.Substring(rootFolderOffset2);

                if (f1 != f2)
                {
                    Log($"Filename diff: '{f1}' '{f2}'");
                    diff = true;
                    if (verboseLogging)
                    {
                        continue;
                    }
                    else
                    {
                        return false;
                    }
                }

                string hash1 = GetFileHash(file1);
                string hash2 = GetFileHash(file2);
                if (hash1 != hash2)
                {
                    Log($"Hash diff: '{file1}' '{file2}' {hash1} {hash2}");
                    diff = true;
                    if (verboseLogging)
                    {
                        continue;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return !diff;
        }

        static string GetFileHash(string filename)
        {
            using (var fs = new FileStream(filename, FileMode.Open))
            {
                using (var bs = new BufferedStream(fs))
                {
                    using (var sha1 = SHA1.Create())
                    {
                        byte[] hash = sha1.ComputeHash(bs);
                        var formatted = new StringBuilder(2 * hash.Length);
                        foreach (byte b in hash)
                        {
                            formatted.AppendFormat("{0:X2}", b);
                        }
                        return formatted.ToString();
                    }
                }
            }
        }

        static string[] GetLocalArtifacts()
        {
            var artifactPaths = GetArtifactPaths();

            var allFiles = new List<string>();

            foreach (var artifactPath in artifactPaths)
            {
                string path, pattern;

                if (artifactPath.Contains(Path.DirectorySeparatorChar) || artifactPath.Contains(Path.AltDirectorySeparatorChar))
                {
                    path = Path.GetDirectoryName(artifactPath);
                    pattern = Path.GetFileName(artifactPath);
                }
                else
                {
                    path = ".";
                    pattern = artifactPath;
                }

                if (!Directory.Exists(path))
                {
                    Log($"Folder not found: '{path}'");
                    continue;
                }

                Log($"Getting files for: '{path}' '{pattern}'");
                var files = Directory.GetFiles(path, pattern, SearchOption.AllDirectories);

                Log($"{files.Length} files found in: '{artifactPath}'");

                allFiles.AddRange(files);
            }

            return allFiles.ToArray();
        }

        static async Task<string[]> DownloadPreviousArtifacts()
        {
            string serverUrl = GetServerUrl();
            string buildConfig = GetBuildConfig();
            (string username, string password) = GetCredentials();

            string previousVersion = Environment.GetEnvironmentVariable("PreviousVersion");
            if (string.IsNullOrEmpty(previousVersion))
            {
                previousVersion = ".lastSuccessful";
            }

            string url = $"{serverUrl}/repository/downloadAll/{buildConfig}/{previousVersion}";

            byte[] content;

            using (var client = new HttpClient())
            {
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                Log($"Getting artifacts: '{url}'");
                content = await client.GetByteArrayAsync(url);
            }

            string filename = "previous_artifacts.zip";

            if (content.Length == 0)
            {
                Log($"Found no previous artifact.");
                return new string[] { };
            }

            Log($"Got artifacts. Length: {content.Length}");
            File.WriteAllBytes(filename, content);

            string folder = Path.GetFileNameWithoutExtension(filename);
            Log($"Extracting: '{filename}' -> '{folder}'");
            ZipFile.ExtractToDirectory(filename, folder);

            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);

            return files;
        }

        static Dictionary<string, string> GetTeamcityBuildVariables()
        {
            string buildpropfile = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_PROPERTIES_FILE");
            if (string.IsNullOrEmpty(buildpropfile))
            {
                Log("Couldn't find Teamcity build properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(buildpropfile))
            {
                Log($"Couldn't find Teamcity build properties file: '{buildpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity build properties file: '{buildpropfile}'");
            var valuesBuild = GetPropValues(buildpropfile);

            return valuesBuild;
        }

        static Dictionary<string, string> GetTeamcityConfigVariables()
        {
            string buildpropfile = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_PROPERTIES_FILE");
            if (string.IsNullOrEmpty(buildpropfile))
            {
                Log("Couldn't find Teamcity build properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(buildpropfile))
            {
                Log($"Couldn't find Teamcity build properties file: '{buildpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity build properties file: '{buildpropfile}'");
            var valuesBuild = GetPropValues(buildpropfile);

            string configpropfile = valuesBuild["teamcity.configuration.properties.file"];
            if (string.IsNullOrEmpty(configpropfile))
            {
                Log("Couldn't find Teamcity config properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(configpropfile))
            {
                Log($"Couldn't find Teamcity config properties file: '{configpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity config properties file: '{configpropfile}'");
            var valuesConfig = GetPropValues(configpropfile);

            return valuesConfig;
        }

        static Dictionary<string, string> GetTeamcityRunnerVariables()
        {
            string buildpropfile = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_PROPERTIES_FILE");
            if (string.IsNullOrEmpty(buildpropfile))
            {
                Log("Couldn't find Teamcity build properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(buildpropfile))
            {
                Log($"Couldn't find Teamcity build properties file: '{buildpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity build properties file: '{buildpropfile}'");
            var valuesBuild = GetPropValues(buildpropfile);

            string runnerpropfile = valuesBuild["teamcity.runner.properties.file"];
            if (string.IsNullOrEmpty(runnerpropfile))
            {
                Log("Couldn't find Teamcity runner properties file.");
                return new Dictionary<string, string>();
            }
            if (!File.Exists(runnerpropfile))
            {
                Log($"Couldn't find Teamcity runner properties file: '{runnerpropfile}'");
                return new Dictionary<string, string>();
            }

            Log($"Reading Teamcity runner properties file: '{runnerpropfile}'");
            var valuesRunner = GetPropValues(runnerpropfile);

            return valuesRunner;
        }

        static Dictionary<string, string> GetPropValues(string filename)
        {
            var rows = File.ReadAllLines(filename);

            var dic = new Dictionary<string, string>();

            foreach (var row in rows)
            {
                int index = row.IndexOf('=');
                if (index != -1)
                {
                    string key = row.Substring(0, index);
                    string value = Regex.Unescape(row.Substring(index + 1));
                    dic[key] = value;
                }
            }

            return dic;
        }

        static string GetServerUrl()
        {
            string server = null;

            var tcVariables = GetTeamcityConfigVariables();

            if (tcVariables.ContainsKey("teamcity.serverUrl"))
            {
                server = tcVariables["teamcity.serverUrl"];
                Log($"Got server from Teamcity: '{server}'");
            }

            if (!server.StartsWith("http://") && !server.StartsWith("https://"))
            {
                server = $"https://{server}";
            }

            return server;
        }

        static string GetBuildConfig()
        {
            string buildConfig = null;

            var tcVariables = GetTeamcityBuildVariables();

            if (tcVariables.ContainsKey("teamcity.buildType.id"))
            {
                buildConfig = tcVariables["teamcity.buildType.id"];
                Log($"Got build config from Teamcity: '{buildConfig}'");
            }

            return buildConfig;
        }

        static string[] GetArtifactPaths()
        {
            string[] artifactPaths = null;

            var tcVariables = GetTeamcityRunnerVariables();

            if (tcVariables.ContainsKey("artefacts.paths"))
            {
                artifactPaths = tcVariables["artefacts.paths"].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var artifactPath in artifactPaths)
                {
                    Log($"Got artifact path from Teamcity: '{artifactPath}'");
                }
            }

            return artifactPaths;
        }

        static (string username, string password) GetCredentials()
        {
            string username = null;
            string password = null;

            var tcVariables = GetTeamcityBuildVariables();

            if (tcVariables.ContainsKey("teamcity.auth.userId"))
            {
                username = tcVariables["teamcity.auth.userId"];
                Log("Got username from Teamcity.");
            }
            if (tcVariables.ContainsKey("teamcity.auth.password"))
            {
                password = tcVariables["teamcity.auth.password"];
                Log("Got password from Teamcity.");
            }

            return (username, password);
        }

        static void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}
