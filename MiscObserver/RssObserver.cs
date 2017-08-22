﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using SQLite.Net;
using System.IO;
using SQLite.Net.Attributes;
using SQLite.Net.Interop;
using SQLite.Net.Platform.Generic;
using SQLite.Net.Platform.Win32;

namespace StalkerProject.MiscObserver
{
    public static class SQLiteHelper
    {
        public static DateTime FromUnixTime(this long unixTime)
        {
            return epoch.AddSeconds(unixTime);
        }
        private static readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToUnixTime(this DateTime date)
        {
            return Convert.ToInt64((date.ToUniversalTime() - epoch).TotalSeconds);
        }
    }

    class MyXmlReader : XmlTextReader
    {
        private bool readingDate = false;
        private readonly string _customUtcDateTimeFormat = "ddd, dd MMM yyyy HH:MM:SS GMT"; // Wed Oct 07 08:00:07 GMT 2009

        public MyXmlReader(Stream s) : base(s) { }

        public MyXmlReader(string inputUri, string customTimeFormat = "") : base(inputUri)
        {
            _customUtcDateTimeFormat = customTimeFormat;
        }

        public override void ReadStartElement()
        {
            if (string.Equals(base.NamespaceURI, string.Empty, StringComparison.InvariantCultureIgnoreCase) &&
                (string.Equals(base.LocalName, "lastBuildDate", StringComparison.InvariantCultureIgnoreCase) ||
                 string.Equals(base.LocalName, "pubDate", StringComparison.InvariantCultureIgnoreCase)))
            {
                readingDate = true;
            }
            base.ReadStartElement();
        }

        public override void ReadEndElement()
        {
            if (readingDate)
            {
                readingDate = false;
            }
            base.ReadEndElement();
        }

        public override string ReadString()
        {
            if (readingDate)
            {
                string dateString = base.ReadString();
                DateTime dt;
                if (!DateTime.TryParse(dateString, out dt))
                    dt = DateTime.ParseExact(dateString, _customUtcDateTimeFormat, CultureInfo.InvariantCulture);
                string result = dt.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
                Console.WriteLine(dateString + " To " + result);
                return result;
            }
            else
            {
                return base.ReadString();
            }
        }
    }

    public class RssObserver : STKWorker
    {
        [Table("FeedData")]
        class RSSData
        {
            [Unique]
            public string GUID { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            [Indexed]
            public DateTime PubTime { get; set; }
        }
        public string URL { get; set; }
        public string CustomTimeFormat { get; set; }
        private SQLiteConnection _conn;

        protected override void Prepare()
        {
            _conn = CreateConnectionForSchemaCreation(Alias + ".db");
        }

        public static SyndicationFeed GetFeed(string uri, string timeFormat = "")
        {
            if (!string.IsNullOrEmpty(uri))
            {
                var ff = new Rss20FeedFormatter(); // for Atom you can use Atom10FeedFormatter()
                var xr = new MyXmlReader(uri,timeFormat);
                ff.ReadFrom(xr);
                return ff.Feed;
            }
            return null;
        }

        public SQLiteConnection CreateConnectionForSchemaCreation(string fileName)
        {
            var conn = new SQLiteConnection(
                (Environment.OSVersion.ToString().IndexOf("Windows")!=-1) ?
                new SQLitePlatformWin32() as ISQLitePlatform : 
                new SQLitePlatformGeneric()
                , fileName);
            conn.CreateTable<RSSData>();
            return conn;
        }

        [Table("FeedData")]
        class FeedGUID
        {
            public string GUID { get; set; }
        }

        protected override void Run()
        {
            base.Run();
            List<string> historyFeeds = new List<string>();
            var result = _conn.Query<FeedGUID>("SELECT GUID FROM FeedData ORDER BY PubTime DESC LIMIT 30");
            foreach (var val in result)
            {
                historyFeeds.Add(val.GUID);
            }
            SyndicationFeed feed;
            try
            {
                feed = GetFeed(URL,CustomTimeFormat);
            }
            catch (Exception e)
            {
                //Network Error
                Console.WriteLine(e);
                Console.WriteLine("Unable to Get Feed:" + URL);
                return;
            }
            string title = feed.Title.Text;
            _conn.RunInTransaction(() =>
            {
                foreach (var synItem in feed.Items.Reverse())
                {
                    if (historyFeeds.IndexOf(synItem.Id) != -1) continue;
                    try
                    {
                        _conn.Insert(new RSSData
                        {
                            Description = synItem.Summary.Text,
                            GUID = synItem.Id,
                            Title = synItem.Title.Text,
                            PubTime = synItem.PublishDate.DateTime.ToUniversalTime()
                        });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        _conn.Insert(new RSSData
                        {
                            Description = synItem.Summary.Text,
                            GUID = synItem.Id + "111",
                            Title = title + " - " + synItem.Title.Text,
                            PubTime = synItem.PublishDate.DateTime.ToUniversalTime()
                        });
                    }
                    DiffDetected?.Invoke(synItem.Id, synItem.Title.Text, synItem.Summary.Text, Alias + ".Updated");
                }
            });
            /*
            using (SQLiteTransaction trans = _conn.BeginTransaction())
            {
                foreach (var synItem in feed.Items.Reverse())
                {
                    if (historyFeeds.IndexOf(synItem.Id) != -1) continue;
                    try
                    {
                        _conn.ExecCommand(
                            "INSERT INTO FeedData(GUID,Title,Desc, PubTime) VALUES(@GUID,@Title,@Desc,@PubTime)",
                            new Dictionary<string, object>
                            {
                                {"@GUID", synItem.Id},
                                {"@Title", synItem.Title.Text},
                                {"@Desc", synItem.Summary.Text},
                                {"@PubTime", synItem.PublishDate.DateTime.ToUnixTime()}
                            });
                    }
                    catch (Exception e)
                    {
                        //Another change will modify even if some commands fail.
                        Console.WriteLine(e);
                    }
                    DiffDetected?.Invoke(synItem.Id,synItem.Title.Text,synItem.Summary.Text,Alias + ".Updated");
                }
                trans.Commit();
            }
            */
        }

        public override void LoadDefaultSetting()
        {
            int randInt = new Random().Next(1, 100000);
            Alias = "RssObserver" + randInt.ToString();
            Interval = 3600000;
            CustomTimeFormat = "ddd, dd MMM yyyy HH:MM:SS GMT";
        }

        public Action<string, string, string, string> DiffDetected { get; set; }
    }
}
