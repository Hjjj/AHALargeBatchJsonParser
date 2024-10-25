using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.IO;

namespace AhaLargeBatchParser
{
    /// <summary>
    /// This program creates a 'to do list' of JSON files to process.
    /// It loops through the list turning each item into a row in a csv file.
    /// It stops at NumberOfRowsPerBatch and creates a csv file of that batch.
    /// </summary>
    class Program
    {
        const string JSON_FILES_TO_PROCESS_TABLE = "FilePaths";

        static void Main(string[] args)
        {
            string directoryPath = @"\\wmodfs01\it\NB_AHA_Cards\100JSON";
            string dbPath = "filePaths.db";

            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"DB not found at {dbPath}");
            }

            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                // Create table if it doesn't exist
                string createTableQuery = $"CREATE TABLE IF NOT EXISTS {JSON_FILES_TO_PROCESS_TABLE} (Id INTEGER PRIMARY KEY, Path TEXT)";
                using (var command = new SQLiteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }

                int ct = GetRowCount(dbPath);

                if(ct == 0)
                {
                    // Loop through files in the directory
                    foreach (var filePath in Directory.GetFiles(directoryPath))
                    {
                        string insertQuery = $"INSERT INTO {JSON_FILES_TO_PROCESS_TABLE} (Path) VALUES (@Path)";
                        using (var command = new SQLiteCommand(insertQuery, connection))
                        {
                            command.Parameters.AddWithValue("@Path", filePath);
                            command.ExecuteNonQuery();
                        }
                    }
                }

                connection.Close();
            }

            Console.WriteLine("File paths have been added to the database.");

            var filePaths = GetAllFilePaths(dbPath);
            Console.WriteLine($"There are {filePaths.Count} file paths in the database.");

            int rowCount = GetRowCount(dbPath);
            Console.WriteLine($"There are {rowCount} rows in the database.");

            Console.ReadKey();
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

    }
}
