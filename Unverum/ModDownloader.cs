using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
// using System.Reflection; // Can be removed if not used
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using SharpCompress.Common;
using System.Text.RegularExpressions;
using SharpCompress.Readers;
using Unverum.UI; // Ensure this is present to access UpdateFileBox and SelectableGameBananaItemFile
using SharpCompress.Archives.SevenZip;
using System.Linq; // For .Any() and .Select()
using SharpCompress.Archives;
using System.Collections.Generic; // For List<>

namespace Unverum
{
    public class ModDownloader
    {
        private string URL_TO_ARCHIVE;
        private string URL;
        private string DL_ID;
        private string MOD_TYPE;
        private string MOD_ID;
        private bool cancelled;
        private HttpClient client = new(); 
        private CancellationTokenSource cancellationToken = new(); 
        private GameBananaAPIV4 response = new(); // For the original Download method
        private ProgressBox progressBox;


        public async void BrowserDownload(string game, GameBananaRecord record)
        {
            UpdateFileBox fileBox = new UpdateFileBox(record.AllFiles, record.Title);
            bool? dialogResult = fileBox.ShowDialog();

            if (dialogResult == true && fileBox.SelectedFiles != null && fileBox.SelectedFiles.Any())
            {
                string targetModFolderName = string.IsNullOrWhiteSpace(fileBox.ModFolderName) ? record.Title : fileBox.ModFolderName;
                targetModFolderName = string.Concat(targetModFolderName.Split(Path.GetInvalidFileNameChars())); // Clean folder name

                foreach (var selectableFile in fileBox.SelectedFiles)
                {
                    GameBananaItemFile fileToDownload = selectableFile.FileInfo; // Access the original GameBananaItemFile
                    if (fileToDownload.DownloadUrl != null && fileToDownload.FileName != null)
                    {
                        var individualCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token);
                        
                        bool downloadSuccess = await DownloadFile(fileToDownload.DownloadUrl, fileToDownload.FileName, new Progress<DownloadProgress>(ReportUpdateProgress), individualCts);
                        
                        if (!cancelled && downloadSuccess) 
                        {
                            await ExtractFile(fileToDownload.FileName, game, record, targetModFolderName, fileToDownload.Description);
                        }
                        if (cancelled) break; 
                    }
                }
            }
        }

        public async void Download(string line, bool running) // This method seems to be for protocol handling
        {
            if (ParseProtocol(line))
            {
                if (await GetData()) // GetData fills 'response'
                {
                    // For protocol download, assume only one specific file is downloaded
                    // and the mod folder name will be the 'response' title.
                    DownloadWindow downloadWindow = new DownloadWindow(response);
                    downloadWindow.ShowDialog();
                    if (downloadWindow.YesNo)
                    {
                        string currentFileName = this.response.Files.FirstOrDefault(f => f.Id == DL_ID)?.FileName;
                        string currentFileDescription = this.response.Files.FirstOrDefault(f => f.Id == DL_ID)?.Description;

                        if (string.IsNullOrEmpty(currentFileName))
                        {
                             MessageBox.Show($"No se pudo encontrar el nombre del archivo para el ID: {DL_ID}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                             if (running) Environment.Exit(0);
                             return;
                        }

                        bool downloadSuccess = await DownloadFile(URL_TO_ARCHIVE, currentFileName, new Progress<DownloadProgress>(ReportUpdateProgress),
                            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token));
                        
                        if (!cancelled && downloadSuccess)
                        {
                            string modFolderName = string.Concat(response.Title.Split(Path.GetInvalidFileNameChars()));
                            await ExtractFile(currentFileName, response.Game.Name.Replace(":", String.Empty), response, modFolderName, currentFileDescription);
                        }
                    }
                }
            }
            if (running)
                Environment.Exit(0);
        }


        private async Task<bool> GetData()
        {
            try
            {
                string responseString = await client.GetStringAsync(URL);
                response = JsonSerializer.Deserialize<GameBananaAPIV4>(responseString);
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show($"Error while fetching data {e.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        
        private void ReportUpdateProgress(DownloadProgress progress)
        {
            if (progressBox == null || progressBox.finished) return;

            if (progress.Percentage == 1)
            {
                progressBox.finished = true;
            }
            progressBox.progressBar.Value = progress.Percentage * 100;
            progressBox.taskBarItem.ProgressValue = progress.Percentage;
            progressBox.progressTitle.Text = $"Descargando {progress.FileName}...";
            progressBox.progressText.Text = $"{Math.Round(progress.Percentage * 100, 2)}% " +
                $"({StringConverters.FormatSize(progress.DownloadedBytes)} de {StringConverters.FormatSize(progress.TotalBytes)})";
        }

        private bool ParseProtocol(string line)
        {
            try
            {
                line = line.Replace("unverum:", "");
                string[] data = line.Split(',');
                URL_TO_ARCHIVE = data[0];
                var match = Regex.Match(URL_TO_ARCHIVE, @"\d*$");
                DL_ID = match.Value;
                MOD_TYPE = data[1];
                MOD_ID = data[2];
                URL = $"https://gamebanana.com/apiv6/{MOD_TYPE}/{MOD_ID}?_csvProperties=_sName,_aGame,_sProfileUrl,_aPreviewMedia,_sDescription,_aSubmitter,_aCategory,_aSuperCategory,_aFiles,_tsDateUpdated,_aAlternateFileSources,_bHasUpdates,_aLatestUpdates";
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show($"Error while parsing {line}: {e.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        // New signature for ExtractFile to accept target mod folder name and specific file description
        private async Task ExtractFile(string downloadedFileName, string game, GameBananaRecord originalRecord, string targetModFolderName, string fileSpecificDescription)
        {
            await Task.Run(() =>
            {
                switch (game) 
                {
                    case "Demon Slayer The Hinokami Chronicles":
                        game = "Demon Slayer";
                        break;
                    case "THE IDOLM@STER STARLIT SEASON":
                        game = "IDOLM@STER";
                        break;
                    case "Dragon Ball: Sparking! ZERO":
                        game = "Dragon Ball Sparking! ZERO";
                        break;
                }
                string _ArchiveSource = $@"{Global.assemblyLocation}{Global.s}Downloads{Global.s}{downloadedFileName}";
                string ArchiveDestination = $@"{Global.assemblyLocation}{Global.s}Mods{Global.s}{game}{Global.s}{targetModFolderName}";
                
                if (File.Exists(_ArchiveSource))
                {
                    try
                    {
                        Directory.CreateDirectory(ArchiveDestination); 

                        if (Path.GetExtension(_ArchiveSource).Equals(".7z", StringComparison.InvariantCultureIgnoreCase))
                        {
                            using (var archive = SevenZipArchive.Open(_ArchiveSource))
                            {
                                var reader = archive.ExtractAllEntries();
                                while (reader.MoveToNextEntry())
                                {
                                    if (!reader.Entry.IsDirectory)
                                        reader.WriteEntryToDirectory(ArchiveDestination, new ExtractionOptions()
                                        {
                                            ExtractFullPath = true,
                                            Overwrite = true
                                        });
                                }
                            }
                        }
                        else
                        {
                            using (Stream stream = File.OpenRead(_ArchiveSource))
                            using (var reader = ReaderFactory.Open(stream))
                            {
                                while (reader.MoveToNextEntry())
                                {
                                    if (!reader.Entry.IsDirectory)
                                    {
                                        reader.WriteEntryToDirectory(ArchiveDestination, new ExtractionOptions()
                                        {
                                            ExtractFullPath = true,
                                            Overwrite = true
                                        });
                                    }
                                }
                            }
                        }
                        
                        string metadataFilePath = $@"{ArchiveDestination}{Global.s}mod.json";
                        Metadata metadata;
                        if (File.Exists(metadataFilePath))
                        {
                            try
                            {
                                metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataFilePath));
                            }
                            catch { metadata = new Metadata(); } 
                        }
                        else
                        {
                            metadata = new Metadata();
                        }

                        metadata.submitter = originalRecord.Owner.Name;
                        metadata.description = originalRecord.Description; 
                        metadata.filedescription = fileSpecificDescription; 
                        metadata.preview = originalRecord.Image;
                        metadata.homepage = originalRecord.Link;
                        metadata.avi = originalRecord.Owner.Avatar;
                        metadata.upic = originalRecord.Owner.Upic;
                        metadata.cat = originalRecord.CategoryName;
                        metadata.caticon = originalRecord.Category.Icon;
                        metadata.lastupdate = originalRecord.DateUpdated;
                        
                        string metadataString = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(metadataFilePath, metadataString);
                    }
                    catch (Exception e)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            MessageBox.Show($"No se pudo extraer {downloadedFileName}: {e.Message}", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning)
                        );
                    }
                }
                
                if (!Directory.Exists(ArchiveDestination) || !Directory.EnumerateFileSystemEntries(ArchiveDestination).Any(entry => !Path.GetFileName(entry).Equals("mod.json", StringComparison.OrdinalIgnoreCase)))
                {
                     Application.Current.Dispatcher.Invoke(() =>
                        MessageBox.Show($"No se extrajo {downloadedFileName} debido a un formato incorrecto o archivo vacío.", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning)
                    );
                }
                else
                {
                    File.Delete(_ArchiveSource); 
                }
            });
        }
        
        // Similar modification for the ExtractFile overload that takes GameBananaAPIV4
        private async Task ExtractFile(string downloadedFileName, string game, GameBananaAPIV4 originalRecord, string targetModFolderName, string fileSpecificDescription)
        {
            await Task.Run(() =>
            {
                switch (game)
                {
                    case "Demon Slayer The Hinokami Chronicles":
                        game = "Demon Slayer";
                        break;
                    case "THE IDOLM@STER STARLIT SEASON":
                        game = "IDOLM@STER";
                        break;
                }
                string _ArchiveSource = $@"{Global.assemblyLocation}{Global.s}Downloads{Global.s}{downloadedFileName}";
                string ArchiveDestination = $@"{Global.assemblyLocation}{Global.s}Mods{Global.s}{game}{Global.s}{targetModFolderName}";

                if (File.Exists(_ArchiveSource))
                {
                    try
                    {
                        Directory.CreateDirectory(ArchiveDestination);

                        if (Path.GetExtension(_ArchiveSource).Equals(".7z", StringComparison.InvariantCultureIgnoreCase))
                        {
                            using (var archive = SevenZipArchive.Open(_ArchiveSource))
                            {
                                var reader = archive.ExtractAllEntries();
                                while (reader.MoveToNextEntry())
                                {
                                    if (!reader.Entry.IsDirectory)
                                        reader.WriteEntryToDirectory(ArchiveDestination, new ExtractionOptions()
                                        {
                                            ExtractFullPath = true,
                                            Overwrite = true
                                        });
                                }
                            }
                        }
                        else
                        {
                            using (Stream stream = File.OpenRead(_ArchiveSource))
                            using (var reader = ReaderFactory.Open(stream))
                            {
                                while (reader.MoveToNextEntry())
                                {
                                    if (!reader.Entry.IsDirectory)
                                    {
                                        reader.WriteEntryToDirectory(ArchiveDestination, new ExtractionOptions()
                                        {
                                            ExtractFullPath = true,
                                            Overwrite = true
                                        });
                                    }
                                }
                            }
                        }
                        
                        string metadataFilePath = $@"{ArchiveDestination}{Global.s}mod.json";
                        Metadata metadata;
                        if (File.Exists(metadataFilePath))
                        {
                             try { metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataFilePath)); }
                             catch { metadata = new Metadata(); }
                        }
                        else
                        {
                            metadata = new Metadata();
                        }

                        metadata.submitter = originalRecord.Owner.Name;
                        metadata.description = originalRecord.Description;
                        metadata.filedescription = fileSpecificDescription; 
                        metadata.preview = originalRecord.Image;
                        metadata.homepage = originalRecord.Link;
                        metadata.avi = originalRecord.Owner.Avatar;
                        metadata.upic = originalRecord.Owner.Upic;
                        metadata.cat = originalRecord.CategoryName;
                        metadata.caticon = originalRecord.Category.Icon;
                        metadata.lastupdate = originalRecord.DateUpdated;
                        string metadataString = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(metadataFilePath, metadataString);
                    }
                    catch (Exception e)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            MessageBox.Show($"No se pudo extraer {downloadedFileName}: {e.Message}", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning)
                        );
                    }
                }

                if (!Directory.Exists(ArchiveDestination) || !Directory.EnumerateFileSystemEntries(ArchiveDestination).Any(entry => !Path.GetFileName(entry).Equals("mod.json", StringComparison.OrdinalIgnoreCase)))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        MessageBox.Show($"No se extrajo {downloadedFileName} debido a un formato incorrecto o archivo vacío.", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning)
                    );
                }
                else
                {
                     File.Delete(_ArchiveSource);
                }
            });
        }

        // Updated DownloadFile to return a boolean indicating success/cancellation
        private async Task<bool> DownloadFile(string uri, string fileName, Progress<DownloadProgress> progress, CancellationTokenSource cancellationTokenSourceLocal)
        {
            cancelled = false; // Reset cancellation state for this download
            try
            {
                Directory.CreateDirectory($@"{Global.assemblyLocation}{Global.s}Downloads");
                string filePath = $@"{Global.assemblyLocation}{Global.s}Downloads{Global.s}{fileName}";

                if (File.Exists(filePath))
                {
                    try { File.Delete(filePath); }
                    catch (Exception e)
                    {
                        MessageBox.Show($"No se pudo eliminar el archivo existente {filePath} ({e.Message})", "Advertencia", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }

                progressBox = new ProgressBox(cancellationTokenSourceLocal); // Use the local CancellationTokenSource
                progressBox.progressBar.Value = 0;
                progressBox.finished = false;
                progressBox.Title = $"Progreso de Descarga";
                
                // Ensure Show() is called on the UI thread
                Application.Current.Dispatcher.Invoke(() => {
                    progressBox.Show();
                    progressBox.Activate();
                });
                
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await client.DownloadAsync(uri, fs, fileName, progress, cancellationTokenSourceLocal.Token);
                }
                
                if (!progressBox.finished) 
                {
                    // Ensure Close() is called on the UI thread
                     Application.Current.Dispatcher.Invoke(() => progressBox.Close());
                }
                return true; // Success
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                if (File.Exists($@"{Global.assemblyLocation}{Global.s}Downloads{Global.s}{fileName}"))
                {
                    File.Delete($@"{Global.assemblyLocation}{Global.s}Downloads{Global.s}{fileName}");
                }
                if (progressBox != null)
                {
                    progressBox.finished = true;
                    Application.Current.Dispatcher.Invoke(() => progressBox.Close());
                }
                return false; // Cancelled
            }
            catch (Exception e)
            {
                if (progressBox != null)
                {
                    progressBox.finished = true;
                    Application.Current.Dispatcher.Invoke(() => progressBox.Close());
                }
                MessageBox.Show($"Error mientras se descargaba {fileName}. {e.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                cancelled = true; 
                return false; // Failure
            }
        }
    }
}
