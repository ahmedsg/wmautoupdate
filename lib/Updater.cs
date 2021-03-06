﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Net;
using System.Reflection;
using System.Xml;
using System.Windows.Forms;
using System.Diagnostics;
using Ionic.Zip;
using System.Threading;
using System.Runtime.InteropServices;

namespace WmAutoUpdate
{
 
  public class Updater
  {
    [DllImport("coredll")]
    protected static extern bool CeRunAppAtTime(string pwszAppName, ref SystemTime lpTime);

    public delegate void UpdateDone();
    public event UpdateDone UpdateDoneEvent;
    private delegate void performUpdateDelegate();

    private const string UPDATE_FOLDER_NAME = "wmautoupdates";
    private const string BACKUP_FOLDER_NAME = "wmautobackups";
    private readonly String URL;
    private readonly String updateFilePath;
    private readonly String appPath;
    private readonly Assembly callingAssembly;
    private string zipFileURL;
    private Notification notification;
    private volatile bool abortUpdate = false;

    public Updater(String url)
    {
      this.URL = url;
      Debug.Assert(url != null);
      callingAssembly = System.Reflection.Assembly.GetCallingAssembly();
      String fullAppName = callingAssembly.GetName().CodeBase;
      appPath = Path.GetDirectoryName(fullAppName);
      updateFilePath = Path.Combine(appPath, "wmautoupdate.xml");
      this.assertPreviousUpdate();
    }

    private void assertPreviousUpdate()
    {
      string backupDir = appPath + "\\" + BACKUP_FOLDER_NAME;
      string updateDir = appPath + "\\" + UPDATE_FOLDER_NAME;

      if (File.Exists(updateFilePath))
        this.removeFile(updateFilePath);
      if (Directory.Exists(updateDir))
        Directory.Delete(updateDir, true);

      if (File.Exists(backupDir + "\\" + "success"))
        Directory.Delete(backupDir, true);
      else
      {
        if (Directory.Exists(backupDir))
        {
        foreach (string f in Directory.GetFiles(backupDir))
        {
          File.Move(f, appPath + "\\" + getFilenameFromPath(f));
        }
        Directory.Delete(backupDir, true);
        }
      }
    }

    public void CheckForNewVersion()
    {
      Stream s;
      TransferManager tm = new TransferManager();
      if (tm.downloadFile(URL, out s, updateFilePath, null))
      {
        s.Close();
        this.showUpdateDialog(s);
        this.cleanup();
      }
    }

    protected bool showUpdateDialog(Stream file)
    {
      Version currentVersion = callingAssembly.GetName().Version;
      XmlDocument xDoc = new XmlDocument();
      xDoc.Load(updateFilePath);
      XmlNodeList modules = xDoc.GetElementsByTagName("download");
      XmlNodeList versions = xDoc.GetElementsByTagName("version");
     
      Version newVersion = new Version(
        int.Parse(versions[0].Attributes["maj"].Value),
        int.Parse(versions[0].Attributes["min"].Value),
        int.Parse(versions[0].Attributes["bld"].Value));

      if (currentVersion.CompareTo(newVersion) < 0)
      {
        Logger.Instance.log("New Version available: " + newVersion.ToString());
        XmlNodeList messages = xDoc.GetElementsByTagName("message");
        XmlNodeList links = xDoc.GetElementsByTagName("link");
        String name = modules[0].Attributes["name"].Value;
        String link = links[0].InnerText;
        String message = messages[0].InnerText;
        this.zipFileURL = link;
        notification = new Notification(name, message, newVersion.ToString(), callingAssembly, this);
        notification.AbortUpdateEvent += new Notification.AbortUpdate(notification_AbortUpdateEvent);
        notification.StartUpdateEvent += new Notification.StartUpdate(startUpdate);

        Logger.Instance.log("Version: " + newVersion);
        Logger.Instance.log("Message: " + message);
        Logger.Instance.log("Link: " + link);

        if (notification.ShowDialog() == DialogResult.No)
        {
          notification.Dispose();
          return false;
        }
        else
        {
          notification.Dispose();
          string backupDir = appPath + "\\" + BACKUP_FOLDER_NAME;
          File.Create(backupDir + "\\" + "success");
          this.restartApp();
          return true;
        }
        
      }
      return true;
      #region other XML parser
      //XmlReaderSettings settings = new XmlReaderSettings();
      //settings.ConformanceLevel = ConformanceLevel.Fragment;
      //settings.IgnoreWhitespace = true;
      //settings.IgnoreComments = true;
      //using (XmlReader reader = XmlReader.Create(file, settings))
      //{
      //  while (reader.Read())
      //  {
      //    if (reader.IsStartElement())
      //    {
      //      if (reader.IsEmptyElement)
      //        Logger.Instance.log(reader.Name);
      //      else
      //      {
      //        Logger.Instance.log(reader.Name);
      //        reader.Read(); // Read the start tag.
      //        if (reader.IsStartElement())  // Handle nested elements.
      //          Logger.Instance.log("\r\n"+ reader.Name);
      //        Logger.Instance.log(reader.ReadString());  //Read the text content of the element.
      //      }
      //    }
      //  }
      //}
      #endregion
    }

    void notification_AbortUpdateEvent()
    {
      this.abortUpdate = true;
    }

    private void startUpdate()
    {
      ThreadPool.QueueUserWorkItem(new WaitCallback(performUpdate));
    }

    private void cleanup()
    {
      string backupDir = appPath + "\\" + BACKUP_FOLDER_NAME;
      string updateDir = appPath + "\\" + UPDATE_FOLDER_NAME;

      this.removeFile(updateFilePath);
      if (Directory.Exists(updateDir))
        Directory.Delete(updateDir, true);
    }

    private void performUpdate(object obj)
    {
      string url = (string)zipFileURL;

      String fullAppName = callingAssembly.GetName().CodeBase;
      String appPath = Path.GetDirectoryName(fullAppName);
      String updateDir = appPath + "\\" + UPDATE_FOLDER_NAME;
      String updateFilename = getFilename(url);
      string updateZip = updateDir + "\\" + updateFilename;
      // Create directory to store update files
      Directory.CreateDirectory(updateDir);
      TransferManager tm = new TransferManager();
      tm.AddObserver(notification);
      Stream s;

      tm.downloadFile(url, out s, updateZip, notification.trans);
      if (s != null)
        s.Close();

      if (!abortUpdate)
      {
        using (ZipFile zip1 = ZipFile.Read(updateZip))
        {
          foreach (ZipEntry e in zip1)
          {
            e.Extract(updateDir, ExtractExistingFileAction.OverwriteSilently);
          }
        }

        File.Delete(updateZip);
        string backupDir = appPath + "\\" + BACKUP_FOLDER_NAME;
        if (Directory.Exists(backupDir))
          Directory.Delete(backupDir, true);
        Directory.CreateDirectory(backupDir);
        foreach (string filepath in Directory.GetFiles(updateDir))
        {
          string originalFile = appPath + "\\" + getFilenameFromPath(filepath);
          if (File.Exists(originalFile))
          {
            string backupFilepath = backupDir + "\\" + getFilenameFromPath(filepath);
            File.Move(originalFile, backupFilepath);
            File.Move(filepath, originalFile);
          }
        }
        OnUpdateDone();
      }
    }

    private string getFilename(String url)
    {
      char[] delim = { '/' };
      string[] a = url.Split(delim);
      return a[a.Length - 1];
    }

    private string getFilenameFromPath(String path)
    {
      char[] delim = { '\\' };
      string[] a = path.Split(delim);
      return a[a.Length - 1];
    }

    protected void OnUpdateDone()
    {
      if (UpdateDoneEvent != null)
      {
        UpdateDoneEvent();
      }
    }

    #region Remove File
    protected bool removeFile(string FileName)
    {
      bool Ret = false;
      try
      {
        if (File.Exists(FileName) == false) { return true; }
        File.Delete(FileName);
        Ret = true;
      }
      catch (Exception) { throw; }
      return Ret;
    }
    #endregion

    private void restartApp()
    {     
      SystemTime timeToLaunch = new SystemTime(DateTime.Now.AddSeconds(11));
      bool res = CeRunAppAtTime(callingAssembly.GetName().CodeBase, ref timeToLaunch);

      Application.Exit();
    }

  }
}
