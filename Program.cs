using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SQLite;
using System.IO;
using Serilog;

namespace AhaLargeBatchParser
{
    /// <summary>
    /// This program creates a 'to do list' of JSON files to process.
    /// It loops through the list turning each item into a row in a csv file.
    /// It stops at NumberOfRowsPerBatch and creates a csv file of that batch.
    /// </summary>
    class Program
    {
        const string JSON_FILES_TO_PROCESS_TABLE = "WorkQueue";
        protected static string dbPath = ConfigurationManager.AppSettings["DbPath"];
        protected static string JSONDirectoryPath = ConfigurationManager.AppSettings["DirectoryPath"];
        protected static string csvFolder = ConfigurationManager.AppSettings["CsvFolder"];
        protected static string logFolder = ConfigurationManager.AppSettings["LogFolder"];

        static void Main(string[] args)
        {
            ConsoleAndLog("Application Started.");

            InitializePaths();
            ConsoleAndLog("Paths initialized.");

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logFolder, "log.txt"))
                .CreateLogger();

            InitializeWorkQueue();
            ConsoleAndLog("Work Queue Ready to Use.");

            ProcessWorkQueue();
            ConsoleAndLog("Work queue completed.");

            ConsoleAndLog("Application finished.");
            Console.ReadKey();
        }

        private static void ProcessWorkQueue()
        {
            //loop through the work queue
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string selectQuery = $"SELECT Path FROM {JSON_FILES_TO_PROCESS_TABLE} WHERE IsComplete=0";
                using (var command = new SQLiteCommand(selectQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        int ct = 0;
                        while (reader.Read())
                        {
                            Console.WriteLine($"{++ct} {reader["Path"].ToString()}");
                        }
                    }
                }

                connection.Close();
            }

        }

        private static void ConsoleAndLog(string message)
        {
            Console.WriteLine(message);
            Log.Information(message);
        }

        private static void InitializeWorkQueue()
        {
            int rowCount = GetRowCount();

            if (rowCount == 0)
            {
                Console.WriteLine($"y|n, Import JSON Tasks to Work Queue?");
                ConsoleKeyInfo response = Console.ReadKey();
                
                if (response.KeyChar == 'y')
                {
                    LoadJsonFilesIntoWorkQueue();
                }
            }
        }

        static void InitializePaths()
        {
            string rootFolder1 = AppDomain.CurrentDomain.BaseDirectory;

            //is the db already there
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"y|n, Create new database at {Path.Combine(rootFolder1, dbPath)}");
                ConsoleKeyInfo response = Console.ReadKey();
                if (response.KeyChar == 'y')
                {
                    using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
                    {
                        connection.Open();

                        // Create table if it doesn't exist
                        string createTableQuery = $"CREATE TABLE IF NOT EXISTS {JSON_FILES_TO_PROCESS_TABLE} (Id INTEGER PRIMARY KEY, Path TEXT, IsComplete INTEGER, Comments TEXT)";
                        using (var command = new SQLiteCommand(createTableQuery, connection))
                        {
                            command.ExecuteNonQuery();
                        }

                        connection.Close();
                    }
                    Console.WriteLine($" DB Created.");
                }
                else
                {
                    Console.WriteLine("Exiting program.");
                    Log.Information("Exiting program because no DB.");
                    Environment.Exit(0);
                }
            }

            if (csvFolder == string.Empty)
            {
                csvFolder = rootFolder1;
            }
            else if (!Directory.Exists(csvFolder))
            {
                Console.WriteLine($"CSV folder does not exist. Any key to exit.");
                Log.Error("CSV folder does not exist. Any key to exit.");
                Console.ReadKey();
                Environment.Exit(0);
            }

            if (logFolder == string.Empty)
            {
                logFolder = rootFolder1;
            }
            else if (!Directory.Exists(logFolder))
            {
                Console.WriteLine($"Log folder does not exist. Any key to exit.");
                Log.Error("Log folder does not exist. Any key to exit.");
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        static int GetRowCount()
        {
            int rowCount = 0;

            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string countQuery = $"SELECT COUNT(*) FROM {JSON_FILES_TO_PROCESS_TABLE}";
                using (var command = new SQLiteCommand(countQuery, connection))
                {
                    rowCount = Convert.ToInt32(command.ExecuteScalar());
                }

                connection.Close();
            }

            return rowCount;
        }

        static void LoadJsonFilesIntoWorkQueue()
        {
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                foreach (string jsonFilePath in Directory.EnumerateFiles(JSONDirectoryPath, "*.json"))
                {
                    string insertQuery = $"INSERT OR IGNORE INTO {JSON_FILES_TO_PROCESS_TABLE} (Path, IsComplete, Comments) VALUES (@Path, @IsComplete, @Comments)";
                    using (var command = new SQLiteCommand(insertQuery, connection))
                    {
                        command.Parameters.AddWithValue("@Path", jsonFilePath);
                        command.Parameters.AddWithValue("@IsComplete", 0);
                        command.Parameters.AddWithValue("@Comments", string.Empty);
                        command.ExecuteNonQuery();
                    }
                }

                connection.Close();
            }
        }

        static List<string> GetAllFilePaths(string dbPath)
        {
            List<string> filePaths = new List<string>();

            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                string selectQuery = $"SELECT Path FROM {JSON_FILES_TO_PROCESS_TABLE}";
                using (var command = new SQLiteCommand(selectQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            filePaths.Add(reader["Path"].ToString());
                        }
                    }
                }

                connection.Close();
            }

            return filePaths;
        }

        static bool IsFullFolderPath(string fileName)
        {
            return Path.GetDirectoryName(fileName) != string.Empty;
        }
    }
}
