This App converts Azure AI Image JSON files to csv files. 
Upon startup the app scans a specified directory of json files and one-by-one converts those files into tab-delimited rows in a txt file. 
After every 5000 rows, the txt file is saved to disk, and then the next txt file is begun until all json files in the directory have been converted. 
