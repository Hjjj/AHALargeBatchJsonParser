using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using CsvHelper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace AhaLargeBatchParser
{
    /// <summary>
    /// This program processes a directory full of AHA Ecard JSONs into a single csv file.
    /// It creates a 'to do list' of JSON files to process.
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
        protected static int CsvBatchSize = int.Parse(ConfigurationManager.AppSettings["CSV_BATCH_SIZE"]);

        static void Main(string[] args)
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(logFolder, "log_csv.txt"))
                .CreateLogger();

            ConsoleAndLog("Application Started.");
            InitializePaths();
            InitializeWorkQueue();
            ProcessWorkQueue();
            ConsoleAndLog("Work queue completed. Application finished");
            Console.ReadKey();
        }

        private static void ProcessWorkQueue()
        {
            //loop through the work queue
            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                // Enable read_uncommitted mode this allows us to read the db while it's being written to
                using (var pragmaCommand = new SQLiteCommand("PRAGMA read_uncommitted = true;", connection))
                {
                    pragmaCommand.ExecuteNonQuery();
                }

                string selectQuery = $"SELECT Path FROM {JSON_FILES_TO_PROCESS_TABLE} WHERE IsComplete=0";

                using (var command = new SQLiteCommand(selectQuery, connection))
                {
                    using (var reader = command.ExecuteReader())
                    {
                        ConsoleAndLog($"BEGIN JSON to CSV conversion loop");
                        var fileNum = 0;

                        List<eCardCsvSheet> eCardCSVList = new List<eCardCsvSheet>();

                        while (reader.Read())
                        {
                            string fileName = reader["Path"].ToString();
                            ConsoleAndLog($"{++fileNum} {fileName}");
                            eCardCsvSheet csvSheet = ConvertJSONToCSV(fileName);
                            
                            if(csvSheet != null)
                            {
                                eCardCSVList.Add(csvSheet);
                            }

                            //save a batch of rowt to a csv file
                            if (eCardCSVList.Count >= CsvBatchSize)
                            {
                                ConsoleAndLog($"Attempting to save a Batch of {CsvBatchSize} to CSV file.");
                                WriteToCSVFile(eCardCSVList);
                                UpdateDbStatus(eCardCSVList, connection);
                                eCardCSVList.Clear();
                                fileNum = 0;
                                ConsoleAndLog($"Batch of {CsvBatchSize} saved to a CSV file.");
                            }
                        } //while reader.read

                        //reader is done finish off the remaining rows
                        WriteToCSVFile(eCardCSVList);
                        UpdateDbStatus(eCardCSVList, connection);
                        ConsoleAndLog($"END JSON to CSV conversion loop");
                    }
                }

                connection.Close();
            }

        }

        private static void WriteToCSVFile(List<eCardCsvSheet> eCardCSVList)
        {
            if (eCardCSVList == null || eCardCSVList.Count == 0)
            {
                ConsoleAndLog($"Error writing to CSV file. eCardCSVList was empty.");
                return;
            }

            string csvFileName = Path.Combine(csvFolder, GetCsvFileName());

            //we convert the list of eCardCsvSheets to a csv file
            try
            {
                using (var writer = new StreamWriter(csvFileName))
                {
                    var config = new CsvHelper.Configuration.CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
                    {
                        Delimiter = "\t" // Set the delimiter to a tab character
                    };

                    using (var csv = new CsvWriter(writer, config))
                    {
                        csv.WriteRecords(eCardCSVList);
                    }
                }
                ConsoleAndLog($"CSV file written as {csvFileName}");
            }
            catch (Exception ex)
            {
                ConsoleAndLog($"Error writing to CSV file - {csvFileName}, {ex.Message}");
            }
        }

        private static void UpdateDbStatus(List<eCardCsvSheet> eCardCSVList, SQLiteConnection connection)
        {
            //loop through each item in ecardcsvlist and update the db

                foreach (var eCardSheet in eCardCSVList)
                {
                    string updateQuery = $"UPDATE {JSON_FILES_TO_PROCESS_TABLE} SET IsComplete=1 WHERE Path=@Path";
                    using (var command = new SQLiteCommand(updateQuery, connection))
                    {
                        command.Parameters.AddWithValue("@Path", eCardSheet.Filename);
                        command.ExecuteNonQuery();
                    }
                }

        }

        private static string GetCsvFileName()
        {
            string timestamp = DateTime.Now.ToString("CSV-yyyy-MM-dd-HH-mm-ss");
            string csvFileName = $"{timestamp}.txt";
            return csvFileName;
        }

        private static eCardCsvSheet ConvertJSONToCSV(string jsonFilePath)
        {
            try
            {
                JObject jsonObject = ReadJsonFile(jsonFilePath);

                //This is a list of objects, each object holds a line of text and it's bounding box
                List<CardTextComponent> componentList = ConvertFromJsonToCardTextComponentList(jsonObject);

                //This is a list of strings, each string is a line of text from the eCard
                List<string> linesOnTheCard = ConvertFromJsonToLineList(jsonObject);

                //This list is a list of the labels that this specific card has, such as 'renew by', 'issue date', 'eCard code', etc.
                var labelList = RetrieveLabels(linesOnTheCard);

                //Lets just make sure the data we have so far makes sense before proceeding
                //we are going to make sure the text and the labels we have are a compatible match
                if (!CardSanityCheck(linesOnTheCard, labelList))
                {
                    ConsoleAndLog($"Error: on {jsonFilePath} - Sanity check failed.");
                    return null;
                }

                //we are populating an object where each field will be a column in the csv file
                eCardCsvSheet csvSheet = FillOutCSVSheet(labelList, componentList, jsonFilePath);

                //each item in this list will be a row in the csv
                return csvSheet;

                //Console.WriteLine($"#{rowNum}-Success {GetFileNameWithoutExtension(jsonFilePath)}");
            }
            catch (Exception ex)
            {
                ConsoleAndLog($"Error: on {jsonFilePath} - {ex.Message}");
            }
            return null;
        }

        private static string FindDataByLabel(
     List<CardTextComponent> componentList,
     String labelName,
     SearchDirection searchDirection = SearchDirection.DOWN)
        {
            String foundStr = string.Empty;

            //validate params
            if (componentList is null || componentList.Count == 0 || string.IsNullOrEmpty(labelName))
            {
                Console.WriteLine($"FindDataByLabel has bad params");
                return foundStr;
            }

            //find the label first 
            System.Drawing.Point labelGeometricCenter = Point.Empty;

            CardTextComponent labelComponent = null;

            foreach (var component in componentList)
            {
                if (component.Text.Contains(labelName))
                {
                    labelComponent = component;
                    break;
                }
            }

            if (labelComponent is null)
            {
                Console.WriteLine($"Label {labelName} not found");
                return foundStr;
            }

            CardTextComponent closestComponent = null;
            int closestDistance = int.MaxValue;

            for (int i = 1; i < componentList.Count; i++)
            {
                CardTextComponent nextComponent = componentList[i];

                //dont even consider data above a label, and dont consider the label itself.

                if (searchDirection == SearchDirection.DOWN)
                {
                    if (nextComponent.GeometricCenter().Y - (int)(nextComponent.TextRectangle.Height / 2) <= labelComponent.GeometricCenter().Y ||
                        nextComponent.Text.Contains(labelName))
                    {
                        continue;
                    }
                }
                else //search UP
                {
                    if (nextComponent.GeometricCenter().Y + (int)(nextComponent.TextRectangle.Height / 2) >= labelComponent.GeometricCenter().Y ||
                        nextComponent.Text.Contains(labelName))
                    {
                        continue;
                    }
                }

                //calculate distance from label
                int distanceFromLabel = CalculateDistance(labelComponent.GeometricCenter(), nextComponent.GeometricCenter());

                if (distanceFromLabel < closestDistance)
                {
                    closestDistance = distanceFromLabel;
                    closestComponent = nextComponent;
                }

            }

            if (closestComponent != null)
            {
                foundStr = closestComponent.Text;
            }

            return foundStr;

        }


        //This method will populate the eCardCsvSheet object with the data from the card image
        private static eCardCsvSheet FillOutCSVSheet(LabelList labelList, List<CardTextComponent> componentList, string filePath)
        {
            var eCardSheet = new eCardCsvSheet();

            eCardSheet.IssueDate = FindDataByLabel(componentList, labelList.IssueDate);
            eCardSheet.RenewByDate = FindDataByLabel(componentList, labelList.RenewBy);
            eCardSheet.EcardCode = FindDataByLabel(componentList, labelList.ECardCode);
            eCardSheet.FullName = FindDataByLabel(componentList, labelList.Name, SearchDirection.UP);
            eCardSheet.Filename = filePath;
            eCardSheet.CertTitle = PullParagraph(componentList, labelList);

            return eCardSheet;
        }

        //do labels and the text on the card match up?
        private static bool CardSanityCheck(List<string> linesOnTheCard, LabelList labelList)
        {
            if (labelList is null)
                return false;
            if (linesOnTheCard.Count == 0)
                return false;
            if (!linesOnTheCard.Contains(labelList.IssueDate))
                return false;
            if (!linesOnTheCard.Contains(labelList.RenewBy))
                return false;
            if (!linesOnTheCard.Contains(labelList.ECardCode))
                return false;
            if (!ListContainsSubstring(linesOnTheCard, labelList.Name))
                return false;

            return true;
        }

        //Each type of card has a set of labels and tokens that are needed to extract the data from the card.
        //I call this set of data a 'LabelList'
        //With these 3 types of labellists, I can parse 93% of the card images in our sample of 100 cards. 
        private static LabelList RetrieveLabels(List<string> fieldList)
        {
            //The standard template
            if (ListContainsSubstring(fieldList, "has successfully completed the cognitive") &&
                (
                ListContainsSubstring(fieldList, "has ") &&
                ListContainsSubstring(fieldList, " Program.")
                ) &&
                fieldList.Contains("Issue Date") &&
                fieldList.Contains("Renew By") &&
                fieldList.Contains("eCard Code"))
            {
                return new LabelList
                {
                    IssueDate = "Issue Date",
                    RenewBy = "Renew By",
                    ECardCode = "eCard Code",
                    Name = "has successfully completed the cognitive and",
                    CertTitleParagraphBegin = "has successfully completed",
                    CertTitleParagraphEnd = " Program.",
                    CertTitleTokenBegin = "Heart Association ",
                    CertTitleTokenEnd = " Program."
                };
            }

            //The standard template with a spaced e Card code
            if (ListContainsSubstring(fieldList, "has successfully completed the cognitive") &&
                (
                ListContainsSubstring(fieldList, "has ") &&
                ListContainsSubstring(fieldList, " Program.")
                ) &&
                fieldList.Contains("Issue Date") &&
                fieldList.Contains("Renew By") &&
                fieldList.Contains("e Card Code"))
            {
                return new LabelList
                {
                    IssueDate = "Issue Date",
                    RenewBy = "Renew By",
                    ECardCode = "e Card Code",
                    Name = "has successfully completed the cognitive and",
                    CertTitleParagraphBegin = "has successfully completed",
                    CertTitleParagraphEnd = " Program.",
                    CertTitleTokenBegin = "Heart Association ",
                    CertTitleTokenEnd = " Program."
                };
            }

            //The Gold Stamp RQI template
            if (ListContainsSubstring(fieldList, "This is to verify that") &&
                ListContainsSubstring(fieldList, "has demonstrated competence in") &&
                ListContainsSubstring(fieldList, "Resuscitation Quality Improvement") &&
                fieldList.Contains("Date of last activity:") &&
                fieldList.Contains("eCredential valid until:") &&
                fieldList.Contains("eCredential number:"))
            {
                return new LabelList
                {
                    IssueDate = "Date of last activity:",
                    RenewBy = "eCredential valid until:",
                    ECardCode = "eCredential number:",
                    Name = "has demonstrated competence in ",
                    CertTitleParagraphBegin = "has demonstrated competence",
                    CertTitleParagraphEnd = "Heart Association Program.",
                    CertTitleTokenBegin = "competence in ",
                    CertTitleTokenEnd = ". Competence has been "
                };
            }

            return null;
        }

        //does the substring exist in any of the strings in the list?
        private static bool ListContainsSubstring(List<string> list, string subString)
        {
            if (list.FirstOrDefault(s => s.Contains(subString)) != null)
            {
                return true;
            }
            return false;
        }

        private static List<string> ConvertFromJsonToLineList(JObject jsonObject)
        {
            var block = jsonObject["Value"]["Read"]["Blocks"][0];
            List<string> eCardStrings = new List<string>();

            foreach (var blockObject in block)
            {
                foreach (var line in blockObject)
                {
                    //var index = 0;

                    foreach (var lineObject in line)
                    {
                        var fieldValue = lineObject["Text"].ToString();
                        eCardStrings.Add(fieldValue);
                        //index++;
                    }

                }

            }
            return eCardStrings;
        }



        private static List<CardTextComponent> ConvertFromJsonToCardTextComponentList(JObject jsonObject)
        {
            var block = jsonObject["Value"]["Read"]["Blocks"][0];
            List<CardTextComponent> textComponents = new List<CardTextComponent>();

            foreach (var blockObject in block)
            {
                foreach (var line in blockObject)
                {
                    foreach (var lineObject in line)
                    {
                        var fieldValue = lineObject["Text"].ToString();
                        var boundingBox = lineObject["BoundingPolygon"];
                        var pointUL = boundingBox[0];
                        var pointUR = boundingBox[1];
                        var pointLL = boundingBox[2];
                        var pointLR = boundingBox[3];
                        var textComponent = new CardTextComponent(
                            fieldValue,
                            int.Parse(pointUL["X"].ToString()),
                            int.Parse(pointUL["Y"].ToString()),
                            int.Parse(pointUR["X"].ToString()),
                            int.Parse(pointUR["Y"].ToString()),
                            int.Parse(pointLL["X"].ToString()),
                            int.Parse(pointLL["Y"].ToString()),
                            int.Parse(pointLR["X"].ToString()),
                            int.Parse(pointLR["Y"].ToString())
                            );
                        textComponents.Add(textComponent);
                    }

                }

            }

            return textComponents;
        }


        private static JObject ReadJsonFile(string filePath)
        {
            using (StreamReader file = File.OpenText(filePath))
            {
                using (JsonTextReader reader = new JsonTextReader(file))
                {
                    JObject jsonObject = (JObject)JToken.ReadFrom(reader);
                    return jsonObject;
                }
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
            ConsoleAndLog($"WorkQueue has {rowCount} items to process.");

            if (rowCount > 0)
                Console.WriteLine($"Some of those could be bad leftover files from previous runs. ");

            Console.WriteLine($"Scan for more files to process in {JSONDirectoryPath}?");
            Console.WriteLine($"Y|N ?");
            ConsoleKeyInfo response = Console.ReadKey();
                
            if (response.KeyChar == 'y')
            {
                LoadJsonFilesIntoWorkQueue();
                Console.WriteLine($"WorkQueue now has {GetRowCount()} items to process.");
            }
        }

        static void InitializePaths()
        {
            string rootFolder1 = AppDomain.CurrentDomain.BaseDirectory;
            ConsoleAndLog($"Root Folder: {rootFolder1}");

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
                        string createTableQuery = $"CREATE TABLE IF NOT EXISTS {JSON_FILES_TO_PROCESS_TABLE} (Id INTEGER PRIMARY KEY, Path TEXT UNIQUE, IsComplete INTEGER, Comments TEXT)";
                        using (var command = new SQLiteCommand(createTableQuery, connection))
                        {
                            command.ExecuteNonQuery();
                        }

                        connection.Close();
                    }
                    ConsoleAndLog($" DB Created at {Path.Combine(rootFolder1, dbPath)}");
                }
                else
                {
                    ConsoleAndLog("Exiting program. There was no DB and user declined to create new one.");
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

                string countQuery = $"SELECT COUNT(*) FROM {JSON_FILES_TO_PROCESS_TABLE} WHERE IsComplete=0";
                using (var command = new SQLiteCommand(countQuery, connection))
                {
                    rowCount = Convert.ToInt32(command.ExecuteScalar());
                }

                connection.Close();
            }

            return rowCount;
        }

        static int JSONCount(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                ConsoleAndLog("Error: Folder path is invalid or does not exist.");
                return 0;
            }

            string[] jsonFiles = Directory.GetFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly);
            return jsonFiles.Length;
        }

        static void LoadJsonFilesIntoWorkQueue()
        {
            ConsoleAndLog($"Loading JSON files into Work Queue. (if any)");
            ConsoleAndLog($"Scanning through {JSONDirectoryPath} files...");

            using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                connection.Open();

                int fileCount = 0;
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

                    if (++fileCount % 1000 == 0)
                    {
                        Console.WriteLine($"Scanned {fileCount} files...");
                    }
                }

                connection.Close();
            }

            ConsoleAndLog($"Scanning complete.");
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

        private static string ExtractSubstringBetweenTokens(string text, string tokenBegin, string tokenEnd)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tokenBegin) || string.IsNullOrEmpty(tokenEnd))
            {
                return string.Empty;
            }

            int startIndex = text.IndexOf(tokenBegin);
            if (startIndex == -1)
            {
                return string.Empty;
            }
            startIndex += tokenBegin.Length;

            int endIndex = text.IndexOf(tokenEnd, startIndex);
            if (endIndex == -1)
            {
                return string.Empty;
            }

            return text.Substring(startIndex, endIndex - startIndex).Trim();
        }

        private static string ConcatenateComponentList(List<CardTextComponent> paragraphComponents)
        {
            if (paragraphComponents is null || paragraphComponents.Count == 0)
            {
                return string.Empty;
            }

            // Concatenate all the text from the components
            StringBuilder concatenatedText = new StringBuilder();

            foreach (var component in paragraphComponents)
            {
                concatenatedText.Append(component.Text).Append(" ");
            }

            // Convert to a single string and remove excess spaces
            string paragraphText = concatenatedText.ToString();
            paragraphText = System.Text.RegularExpressions.Regex.Replace(paragraphText, @"\s+", " ").Trim();

            return paragraphText;
        }

        private static List<CardTextComponent> GetComponentsWithinRectangle(List<CardTextComponent> componentList, Rectangle paragraphRect)
        {
            List<CardTextComponent> retList = new List<CardTextComponent>();

            foreach (CardTextComponent c in componentList)
            {
                //see if the center of this object lies within the rect
                if (paragraphRect.Contains(c.GeometricCenter()))
                {
                    retList.Add(c);
                }
            }

            return retList.OrderBy(c => c.TextRectangle.Y).ToList();
        }

        private static CardTextComponent GetComponentByStringFragment(List<CardTextComponent> componentList, string fragment)
        {
            foreach (var component in componentList)
            {
                if (component.Text.Contains(fragment))
                {
                    return component;
                }
            }
            return null;
        }

        //give this function any two snippets of text, and it will return a rectangle that contains both snippets
        private static Rectangle CalculateRectangle(CardTextComponent beginningComponent, CardTextComponent endingComponent)
        {
            Rectangle r = new Rectangle(
                beginningComponent.TextRectangle.X,
                beginningComponent.TextRectangle.Y,
                (endingComponent.TextRectangle.X - beginningComponent.TextRectangle.X) + endingComponent.TextRectangle.Width,
                (endingComponent.TextRectangle.Y - beginningComponent.TextRectangle.Y) + endingComponent.TextRectangle.Height
                );

            return r;
        }

        //This code is smart enough to find the paragraph that contains the cert title, and then extract the
        //cert title from that paragraph.
        private static String PullParagraph(List<CardTextComponent> componentList, LabelList labelList)
        {
            //find the first line of the paragraph that contains cert title
            CardTextComponent beginningComponent = GetComponentByStringFragment(componentList, labelList.CertTitleParagraphBegin);

            //Find the last line
            CardTextComponent endingComponent = GetComponentByStringFragment(componentList, labelList.CertTitleParagraphEnd);

            //define a rectangle that contains the entire paragraph
            Rectangle paragraphRect = CalculateRectangle(beginningComponent, endingComponent);

            //find all strings that lie within paragraph rectangle
            List<CardTextComponent> paragraphComponents = GetComponentsWithinRectangle(componentList, paragraphRect);

            //concatenate all the strings into one cohesive string 
            string paragraphText = ConcatenateComponentList(paragraphComponents);

            //pull the cert title from the paragraph
            string returnString = ExtractSubstringBetweenTokens(paragraphText, labelList.CertTitleTokenBegin, labelList.CertTitleTokenEnd);

            return returnString;
        }



        private static int CalculateDistance(Point point1, Point point2)
        {
            int deltaX = point1.X - point2.X;
            int deltaY = point1.Y - point2.Y;
            return (int)Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        static bool IsFullFolderPath(string fileName)
        {
            return Path.GetDirectoryName(fileName) != string.Empty;
        }
        static int CountJsonFilesInDirectory(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                ConsoleAndLog($"Directory does not exist: {directoryPath}");
                return 0;
            }

            int jsonFileCount = Directory.EnumerateFiles(directoryPath, "*.json").Count();
            ConsoleAndLog($"Found {jsonFileCount} JSON files in directory: {directoryPath}");
            return jsonFileCount;
        }
    }

    //represents one piece of text on an eCard. The ai OCR scans the eCard and returns a list of these substrings and their bounding boxes
    public class CardTextComponent
    {
        public string Text { get; set; }
        public Rectangle TextRectangle { get; set; }

        public CardTextComponent(string text, int upperLeftX, int upperLeftY, int upperRightX, int upperRightY, int lowerRightX, int lowerRightY, int lowerLeftX, int lowerLeftY)
        {
            Text = text;
            int minX = Math.Min(Math.Min(upperLeftX, upperRightX), Math.Min(lowerRightX, lowerLeftX));
            int minY = Math.Min(Math.Min(upperLeftY, upperRightY), Math.Min(lowerRightY, lowerLeftY));
            int maxX = Math.Max(Math.Max(upperLeftX, upperRightX), Math.Max(lowerRightX, lowerLeftX));
            int maxY = Math.Max(Math.Max(upperLeftY, upperRightY), Math.Max(lowerRightY, lowerLeftY));
            TextRectangle = new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        public Point GeometricCenter()
        {
            int centerX = TextRectangle.Left + (TextRectangle.Width / 2);
            int centerY = TextRectangle.Top + (TextRectangle.Height / 2);
            return new Point(centerX, centerY);
        }
    }

    //represents a list of all the labels and tokens that are needed to extract data from an eCard
    public class LabelList
    {
        public string IssueDate { get; set; }
        public string RenewBy { get; set; }
        public string ECardCode { get; set; }
        public string Name { get; set; }
        public string CertTitle { get; set; }
        public string CertTitleParagraphBegin { get; set; }
        public string CertTitleParagraphEnd { get; set; }
        public string CertTitleTokenBegin { get; set; }
        public string CertTitleTokenEnd { get; set; }
    }

    //represents a row in the csv file
    public class eCardCsvSheet
    {
        public string CertTitle { get; set; }
        public string FullName { get; set; }
        public string IssueDate { get; set; }
        public string RenewByDate { get; set; }
        public string EcardCode { get; set; }
        public string Filename { get; set; }
    }

    public enum SearchDirection
    {
        UP,
        DOWN
    }
}
