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

        static void Main(string[] args)
        {
            string directoryPath = ConfigurationManager.AppSettings["DirectoryPath"];
            string dbPath = ConfigurationManager.AppSettings["DbPath"];
            string csvFolder = ConfigurationManager.AppSettings["CsvFolder"];
            string logFolder = ConfigurationManager.AppSettings["LogFolder"];

            string rootFolder = AppDomain.CurrentDomain.BaseDirectory;
            InitializePaths(dbPath, csvFolder, logFolder);

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logFolder, "log.txt"))
                .CreateLogger();

            Log.Information("Application started.");

            var filePaths = GetAllFilePaths(dbPath);
            Log.Information($"There are {filePaths.Count} file paths in the Work Queue.");

            int rowCount = GetRowCount(dbPath);
            Log.Information($"There are {rowCount} rows in the database.");

            Log.Information("Application finished.");
            Console.ReadKey();
        }

        static void InitializePaths(string dbPath, string csvFolder, string logFolder)
        {
            string rootFolder = AppDomain.CurrentDomain.BaseDirectory;

            //is the db already there
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"y|n, Create new database at {Path.Combine(rootFolder, dbPath)}");
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
                    Console.WriteLine($"DB Created.");
                }
                else
                {
                    Console.WriteLine("Exiting program.");
                    Environment.Exit(0);
                }
            }

            if (csvFolder == string.Empty)
            {
                csvFolder = rootFolder;
            }
            else if (!Directory.Exists(csvFolder))
            {
                Console.WriteLine($"CSV folder does not exist. Any key to exit.");
                Console.ReadKey();
                Environment.Exit(0);
            }

            if (logFolder == string.Empty)
            {
                logFolder = rootFolder;
            }
            else if (!Directory.Exists(logFolder))
            {
                Console.WriteLine($"Log folder does not exist. Any key to exit.");
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        static int GetRowCount(string dbPath)
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
