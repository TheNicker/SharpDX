﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using HtmlAgilityPack;
using ICSharpCode.SharpZipLib.Zip;

using SharpCore;
using SharpCore.MTPS;

using SharpCore.Logging;

namespace SharpGen.Doc
{
    internal class DocProviderMsdn : DocProvider
    {
        private static Regex stripSpace = new Regex(@"[\r\n]+\s+", RegexOptions.Multiline);
        private static Regex beginWithSpace = new Regex(@"^\s+");
        private Dictionary<Regex, string> mapReplaceName;
        private ZipFile _zipFile;
        private bool isZipUpdated;
        private string archiveFullPath;

        private int filesAddedToArchive = 0;

        public DocProviderMsdn()
        {
            ArchiveName = "MSDNDoc.zip";
            UseArchive = true;
            mapReplaceName = new Dictionary<Regex, string>();
            ReplaceName("IDirectSound(?<name>[3A-Za-z]*)::(?<method>.*)$", @"IDirectSound${name}8::${method}");
            ReplaceName("IDirectSound(?<name>[3A-Za-z]*)$", @"IDirectSound${name}8");
            ReplaceName("IDirectInput(?<name>[A-Za-z]*)([0-9]?|[0-9]A)::(?<method>.*)$", @"IDirectInput${name}8::${method}");
            ReplaceName("IDirectInput(?<name>[A-Za-z]*)([0-9]?|[0-9]A)$", @"IDirectInput${name}8");
            ReplaceName("W::", @"::");
            ReplaceName("([a-z0-9])A::", @"$1::");
            ReplaceName("W$", @"");
            ReplaceName("^_+", @"");
        }

        public void ReplaceName(string fromNameRegex, string toName)
        {
            mapReplaceName.Add(new Regex(fromNameRegex), toName);
        }

        /// <summary>
        /// Archive to use to save the documentation
        /// </summary>
        public string ArchiveName { get; set; }

        /// <summary>
        /// Output path for the archive / Directory
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Set to true to use a zip for cacing documentation
        /// </summary>
        public bool UseArchive { get; set; }

        /// <summary>
        /// Begin to request MSDN
        /// </summary>
        public void Begin()
        {
            filesAddedToArchive = 0;
            string fullPath = (OutputPath ?? ".") + Path.DirectorySeparatorChar + ArchiveName;

            string outputDirectory = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            if (UseArchive)
            {
                archiveFullPath = outputDirectory + Path.DirectorySeparatorChar + ArchiveName;
                OpenArchive();
            }
        }

        /// <summary>
        /// End request to MSDN. Archive is saved if any updated occured between Begin/End.
        /// </summary>
        public void End()
        {
            if (UseArchive)
            {
                CloseArchive();
            }
        }

        private void OpenArchive()
        {
            if (_zipFile == null)
            {
                isZipUpdated = false;
                var fileInfo = new FileInfo(archiveFullPath);
                if (fileInfo.Exists && fileInfo.Length > 0)
                {
                    _zipFile = new ZipFile(archiveFullPath);
                }
                else
                {
                    File.Delete(archiveFullPath);
                    _zipFile = ZipFile.Create(archiveFullPath);
                }
            }
        }

        private void CloseArchive(bool clone = false)
        {
            if (_zipFile != null)
            {
                if (isZipUpdated)
                    _zipFile.CommitUpdate();
                _zipFile.Close();
                if (isZipUpdated && clone)
                    File.Copy(archiveFullPath, archiveFullPath + ".backup", true);
                _zipFile = null;
            }
        }

        private int counter = 0;


        /// <summary>
        /// Get the documentation for a particular prefix (include name) and a full name item
        /// </summary>
        /// <param name="prefixName"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public DocItem FindDocumentation(string name)
        {
            string oldName = name;
            // Regex replacer
            foreach (var keyValue in mapReplaceName)
            {
                if (keyValue.Key.Match(name).Success)
                {
                    name = keyValue.Key.Replace(name, keyValue.Value);
                    break;
                }
            }

            // Handle name with ends A or W
            if (name.EndsWith("A") || name.EndsWith("W"))
            {
                string previouewChar = new string(name[name.Length - 2], 1);

                if (previouewChar.ToUpper() != previouewChar)
                {
                    name = name.Substring(0, name.Length - 1);
                }
            }
            if (oldName != name)
            {
                Console.WriteLine("Documentation: Use name [{0}] instead of [{1}]", name, oldName);
            }

            Logger.Progress(20 + (counter/50) % 10, "Applying C++ documentation ([{0}])", name);

            string doc = GetDocumentationFromCacheOrMsdn(name);
            return ParseDocumentation(doc);
        }

        /// <summary>
        /// Internal ZipEntryStreamSource in order to add a string to a zip
        /// </summary>
        internal class ZipEntryStreamSource : IStaticDataSource
        {
            private Stream stream;
            public ZipEntryStreamSource(string doc)
            {
                byte[] byteArray = Encoding.ASCII.GetBytes( doc );
                stream = new MemoryStream( byteArray ); 
            }

            public Stream GetSource()
            {
                return stream;
            }
        }

        /// <summary>
        /// Handles documentation from zip/directory
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private string GetDocumentationFromCacheOrMsdn(string name)
        {
            string fileName = name.Replace("::", "-") + ".html";

            string doc;

            if (UseArchive)
            {
                OpenArchive();
                var zipEntry = _zipFile.GetEntry(fileName);
                if (zipEntry != null)
                {
                    var streamInput = new StreamReader(_zipFile.GetInputStream(zipEntry));
                    doc = streamInput.ReadToEnd();
                    streamInput.Close();
                }
                else
                {
                    // Begin update if zip is not updated
                    if (!isZipUpdated)
                    {
                        _zipFile.BeginUpdate();
                        isZipUpdated = true;
                    }

                    doc = GetDocumentationFromMsdn(name);
                    
                    _zipFile.Add(new ZipEntryStreamSource(doc), fileName);

                    // Commit update every 20 files
                    filesAddedToArchive++;
                    if ((filesAddedToArchive % 20) == 0)
                    {
                        // Force a Flush of the archive
                        CloseArchive(true);
                    }
                }
            } else
            {
                fileName = OutputPath + Path.DirectorySeparatorChar + fileName;

                if (!File.Exists(fileName))
                {
                    doc = GetDocumentationFromMsdn(name);
                    File.WriteAllText(fileName, doc);
                }
                else
                {
                    doc = File.ReadAllText(fileName);
                }
            }
            return doc;
        }


        private static string ParseSubNodes(HtmlNode htmlNode, bool isRoot)
        {
            StringBuilder documentation = new StringBuilder();

            if (!isRoot)
            {
                if (htmlNode.Name.ToLower() == "dl")
                    documentation.Append("<list>\r\n");
                else if (htmlNode.Name.ToLower() == "dt")
                    documentation.Append("<item><term>");
                else if (htmlNode.Name.ToLower() == "dd")
                    documentation.Append("<description>");
                else if (htmlNode.Name.ToLower() == "p")
                    documentation.Append("<para>");
            }

            if (htmlNode.Name == "a")
            {
                StringBuilder inside = new StringBuilder();
                foreach (var node in htmlNode.ChildNodes)
                    inside.Append(ParseSubNodes(node, false).Trim());
                string insideStr = inside.ToString();

                if (!string.IsNullOrEmpty(insideStr) && insideStr != "Copy")
                {
                    documentation.Append("{{");
                    insideStr = insideStr.Trim().Split(' ','\t')[0];
                    documentation.Append(insideStr);
                    documentation.Append("}}");
                }
                return documentation.ToString();
            }
            else if (htmlNode.Name == "pre")
            {
                return "\r\n<code>\r\n" + ParseSubNodes(htmlNode.FirstChild, false) + "\r\n</code>\r\n";
            }
            else if (htmlNode.NodeType == HtmlNodeType.Text)
            {
                string text = htmlNode.InnerText;
                if (beginWithSpace.Match(text).Success)
                    text = beginWithSpace.Replace(text, " ");
                if (stripSpace.Match(text).Success)
                    text = stripSpace.Replace(text, " ");
                return text;
            }

            foreach (var node in htmlNode.ChildNodes)
            {
                string text = ParseSubNodes(node, false);
                //if (documentation.Length > 0 && documentation[documentation.Length - 1] == '.' && !string.IsNullOrEmpty(text))
                //{
                //    documentation.Append(" ");
                //}

                documentation.Append(text);
            }

            if (!isRoot)
            {
                if (htmlNode.Name.ToLower() == "dl")
                    documentation.Append("</list>\r\n");
                else if (htmlNode.Name.ToLower() == "dt")
                    documentation.Append("</term>");
                else if (htmlNode.Name.ToLower() == "dd")
                    documentation.Append("</description></item>\r\n");
                else if (htmlNode.Name.ToLower() == "p")
                    documentation.Append("</para>\r\n");
            }

            //if (htmlNode.Name == "p")
            //    documentation.Append("\r\n");

            return documentation.ToString();            
        }

        private static Regex regexCapitals = new Regex(@"([^0-9A-Za-z_:\{])([A-Z][A-Z0-9_][0-9A-Za-z_:]*)");
        private static readonly Regex RegexReplacePointer = new Regex(@"pointer");     


        /// <summary>
        /// Parse HtmlNode to extract a string from it. Replace anchors href with {{ }} 
        /// and code with [[ ]]
        /// </summary>
        /// <param name="htmlNode"></param>
        /// <returns></returns>
        private static string ParseNode(HtmlNode htmlNode)
        {
            var result = ParseSubNodes(htmlNode, true);
            result = regexCapitals.Replace(result, "$1{{$2}}");
            result = RegexReplacePointer.Replace(result, "reference");
            result = result.Trim();
            return result;
        }

        private static string GetTextUntilNextHeader(HtmlNode htmlNode)
        {
            htmlNode = htmlNode.NextSibling;

            StringBuilder builder = new StringBuilder();
            while (htmlNode != null && htmlNode.Name != "h3")
            {
                builder.Append(ParseNode(htmlNode));
                htmlNode = htmlNode.NextSibling;
            }

            return builder.ToString();
        }

        private static string ParseNextDiv(HtmlNode htmlNode)
        {
            while (htmlNode != null)
            {
                if (htmlNode.Name == "div")
                    return ParseNode(htmlNode);
                htmlNode = htmlNode.NextSibling;
            }
            return "";
        }

        /// <summary>
        /// Parse a MSDN documentation file
        /// </summary>
        /// <param name="documentationToParse"></param>
        /// <returns></returns>
        public static DocItem ParseDocumentation(string documentationToParse)
        {
            if (string.IsNullOrEmpty(documentationToParse))
                return new DocItem();

            var htmlDocument = new HtmlDocument();
            //            htmlDocument.Load("Documentation\\d3d11-ID3D11Device-CheckCounter.html");
            htmlDocument.LoadHtml(documentationToParse);

            var item = new DocItem { Id = htmlDocument.DocumentNode.ChildNodes.FindFirst("id").InnerText };

            var element = htmlDocument.GetElementbyId("mainSection");

            // Page not found?
            if (element == null)
                return item;

            var firstNode = element.ChildNodes.FindFirst("p");
            if (firstNode != null)
                item.Description = ParseNode(firstNode);

            HtmlNode firstElement = element.ChildNodes.FindFirst("dl");
            if (firstElement != null)
            {
                List<string> currentDoc = new List<string>();
                var nodes = firstElement.ChildNodes;
                int ddCount = 0;
                foreach (HtmlNode htmlNode in nodes)
                {
                    if (htmlNode.Name == "dt")
                    {
                        if (currentDoc.Count > 0)
                        {
                            item.Items.Add(currentDoc[currentDoc.Count - 1]);
                            currentDoc.Clear();
                        }
                    }
                    else if (htmlNode.Name == "dd")
                    {
                        currentDoc.Add(ParseNode(htmlNode));
                    }
                }
                if (currentDoc.Count > 0)
                    item.Items.Add(currentDoc[currentDoc.Count - 1]);
            }
            var headerCollection = element.SelectNodes("//h3");
            if (headerCollection != null)
            {
                foreach (HtmlNode htmlNode in headerCollection)
                {
                    string text = ParseNode(htmlNode);
                    if (text.StartsWith("Remarks"))
                        item.Remarks = GetTextUntilNextHeader(htmlNode);
                    else if (text.StartsWith("Return"))
                        item.Return = GetTextUntilNextHeader(htmlNode);
                }
            }
            else
            {
                var returnCollection = element.SelectNodes("//h4[contains(.,'Return')]");
                if (returnCollection != null)
                    item.Return = ParseNextDiv(returnCollection[0].NextSibling);

                var remarksCollection = element.SelectNodes("//a[@id='remarks']");
                if (remarksCollection != null)
                    item.Remarks = ParseNextDiv(remarksCollection[0].NextSibling);
            }
            return item;
        }

        private static Regex regexUrlMoved = new Regex(@"This content has moved to\s+<a href=""(.*?)\""");

        /// <summary>
        /// Get MSDN documentation using an http query
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns></returns>
        private static string GetDocumentationFromMsdn(string name)
        {

            var shortId = GetShortId(name);
            if (string.IsNullOrEmpty(shortId))
                return string.Empty;

            var result = GetDocFromMTPS(shortId);
            if (string.IsNullOrEmpty(result))
                return string.Empty;
            return "<id>"+shortId+"</id>\r\n" + result;
        }

        private static ContentServicePortTypeClient proxy;

        public static string GetDocFromMTPS(string shortId)
        {
            try
            {
                if (proxy == null) 
                    proxy = new ContentServicePortTypeClient();

                var request = new getContentRequest
                    {
                        contentIdentifier = shortId,
                        locale = "en-us",
                        version = "VS.85",
                        requestedDocuments = new[] { new requestedDocument() { type = documentTypes.primary, selector = "Mtps.Xhtml" } }
                    };
                var response = proxy.GetContent(new appId() { value = "Sandcastle" }, request);
                if (response.primaryDocuments[0].Any != null)
                    return response.primaryDocuments[0].Any.OuterXml;
            } catch (Exception ex)
            {
                Logger.Warning("MTPS error for id {0} : {1}", shortId, ex.Message);
            }
            return string.Empty;
        }



        private static Regex matchId = new Regex(@"/([a-zA-Z0-9]+)(\(.+\).*|\.[a-zA-Z]+)?$");

        public static string GetShortId(string name)
        {
            try
            {
                var url = "http://social.msdn.microsoft.com/Search/en-US?query=" + HttpUtility.UrlEncode(name) + "&refinement=117&addenglish=1";

                var result = GetFromUrl(url);

                if (string.IsNullOrEmpty(result))
                    return string.Empty;

                var doc = new HtmlDocument();
                doc.LoadHtml(result);

                var nodes = doc.DocumentNode.SelectNodes("//div[@class='ResultItem']//td[@class='result']/a");

                // Select 1st node
                foreach (var node in nodes)
                {
                    var text = HttpUtility.HtmlDecode(node.InnerText);
                    if (text.Contains(name))
                    {
                        var hrefAttribute = node.Attributes["href"];
                        if (hrefAttribute != null)
                        {
                            var contentUrl = hrefAttribute.Value;
                            var match = matchId.Match(contentUrl);
                            if (match.Success)
                                return match.Groups[1].Value;
                        }
                    }
                }
            } 
            catch (Exception ex)
            {
                Logger.Warning("Unable to get id for [{0}]", name);
            }

            return string.Empty;
        }

        private static bool IsPageNotFound(string value)
        {
            return value.Contains("Page Not Found");
        }

        private static string GetFromUrlHandlingMove(string url)
        {
            string result = GetFromUrl(url);

            if (IsPageNotFound(result))
                return null;

            var matchMoved = regexUrlMoved.Match(result);
            if (matchMoved.Success)
            {
                result = GetFromUrl(matchMoved.Groups[1].Value);
            }

            if (IsPageNotFound(result))
                return null;

            return result;
        }

        internal static string GetFromUrl(string url)
        {
            try
            {
                // Create web request
                var request = (HttpWebRequest)WebRequest.Create(url);

                // Set value for request headers

                request.Method = "GET";
                request.ProtocolVersion = HttpVersion.Version11;
                request.AllowAutoRedirect = true;
                request.Accept = "*/*";
                request.UserAgent = "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1; SV1; .NET CLR 2.0.50727)";
                request.Headers.Add("Accept-Language", "en-us");
                request.KeepAlive = true;

                StreamReader responseStream = null;
                HttpWebResponse webResponse = null;
                string webResponseStream = string.Empty;

                // Get response for http web request
                webResponse = (HttpWebResponse)request.GetResponse();
                responseStream = new StreamReader(webResponse.GetResponseStream());
                webResponseStream = responseStream.ReadToEnd();
                /*This content has moved to <a href=\"http://msdn.microsoft.com/en-us/library/microsoft.directx_sdk.reference.dideviceobjectinstance(v=VS.85).aspx?appId=Dev10IDEF1&amp;l=ENUS&amp;k=kDIDEVICEOBJECTINSTANCE);k(DevLang-&quot;C++&quot;);k(TargetOS-WINDOWS)&amp;rd=true\"
                                */
                return webResponseStream;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            return string.Empty;
        }
    }
}
