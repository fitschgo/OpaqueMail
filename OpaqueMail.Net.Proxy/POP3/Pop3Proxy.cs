﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.XPath;

namespace OpaqueMail.Net.Proxy
{
    public class Pop3Proxy
    {
        #region Structs
        /// <summary>
        /// Arguments passed in when instantiating a new POP3 proxy instance.
        /// </summary>
        public struct Pop3ProxyArguments
        {
            /// <summary>Certificate to authenticate the server.</summary>
            public X509Certificate Certificate;
            /// <summary>Local IP address to bind to.</summary>
            public IPAddress LocalIpAddress;
            /// <summary>Local IP address to listen on.</summary>
            public int LocalPort;
            /// <summary>Whether the local server supports TLS/SSL.</summary>
            public bool LocalEnableSsl;
            /// <summary>Remote server hostname to forward all POP3 messages to.</summary>
            public string RemoteServerHostName;
            /// <summary>Remote server port to connect to.</summary>
            public int RemoteServerPort;
            /// <summary>Whether the remote POP3 server requires TLS/SSL.</summary>
            public bool RemoteServerEnableSsl;
            /// <summary>(Optional) Credentials to be used for all connections to the remote POP3 server.  When set, this overrides any credentials passed locally.</summary>
            public NetworkCredential RemoteServerCredential;

            /// <summary>The file where events and exception information should be logged.</summary>
            public string LogFile;

            /// <summary>POP3 Proxy to start.</summary>
            public Pop3Proxy Proxy;
        }

        /// <summary>
        /// Arguments passed in when instantiating a new POP3 proxy connection instance.
        /// </summary>
        public struct Pop3ProxyConnectionArguments
        {
            /// <summary>TCP connection to the client.</summary>
            public TcpClient TcpClient;
            /// <summary>Certificate to authenticate the server.</summary>
            public X509Certificate Certificate;
            /// <summary>Local IP address to bind to.</summary>
            public IPAddress LocalIpAddress;
            /// <summary>Local IP address to listen on.</summary>
            public int LocalPort;
            /// <summary>Whether the local server supports TLS/SSL.</summary>
            public bool LocalEnableSsl;
            /// <summary>Remote server hostname to forward all POP3 messages to.</summary>
            public string RemoteServerHostName;
            /// <summary>Remote server port to connect to.</summary>
            public int RemoteServerPort;
            /// <summary>Whether the remote POP3 server requires TLS/SSL.</summary>
            public bool RemoteServerEnableSsl;
            /// <summary>(Optional) Credentials to be used for all connections to the remote POP3 server.  When set, this overrides any credentials passed locally.</summary>
            public NetworkCredential RemoteServerCredential;

            /// <summary>A unique connection identifier for logging.</summary>
            public string ConnectionId;
        }

        /// <summary>
        /// Arguments passed when processing a message.
        /// </summary>
        public struct ProcessMessageArguments
        {
            /// <summary>The text of the message to process.</summary>
            public string MessageText;

            /// <summary>A unique connection identifier for logging.</summary>
            public string ConnectionId;
        }

        /// <summary>
        /// Arguments passed when relaying commands between two connections.
        /// </summary>
        public struct TransmitArguments
        {
            /// <summary>Stream to read commands from.</summary>
            public Stream ClientStream;
            /// <summary>Stream to rebroadcast commands to.</summary>
            public Stream RemoteServerStream;

            /// <summary>Whether the target of this invocation is the client.</summary>
            public bool IsClient;

            /// <summary>A unique connection identifier for logging.</summary>
            public string ConnectionId;
        }
        #endregion Structs

        #region Public Members
        /// <summary>Welcome message to be displayed when connecting.</summary>
        public string WelcomeMessage = "OpaqueMail Proxy";
        #endregion Public Members

        #region Private Members
        /// <summary>Whether the proxy has been started.</summary>
        private bool Started = false;
        /// <summary>A TcpListener to accept incoming connections.</summary>
        private TcpListener Listener;
        /// <summary>A unique session identifier for logging.</summary>
        private string SessionId = "";
        /// <summary>A unique connection identifier for logging.</summary>
        private int ConnectionId = 0;
        /// <summary>StreamWriter object to output event logs and exception information.</summary>
        private StreamWriter LogWriter = null;
        /// <summary>A collection of all S/MIME signing certificates imported during this session.</summary>
        public X509Certificate2Collection SmimeCertificatesReceived = new X509Certificate2Collection();
        #endregion Private Members

        #region Public Methods
        /// <summary>
        /// Start a POP3 proxy instance.
        /// </summary>
        /// <param name="localIPAddress">Local IP address to bind to.</param>
        /// <param name="localPort">Local port to listen on.</param>
        /// <param name="localEnableSsl">Whether the local server supports TLS/SSL.</param>
        /// <param name="remoteServerHostName">Remote server hostname to forward all POP3 messages to.</param>
        /// <param name="remoteServerPort">Remote server port to connect to.</param>
        /// <param name="remoteServerEnableSsl">Whether the remote POP3 server requires TLS/SSL.</param>
        public async void StartProxy(IPAddress localIPAddress, int localPort, bool localEnableSsl, string remoteServerHostName, int remoteServerPort, bool remoteServerEnableSsl)
        {
            await StartProxy(localIPAddress, localPort, localEnableSsl, remoteServerHostName, remoteServerPort, remoteServerEnableSsl, null, "");
        }

        /// <summary>
        /// Start a POP3 proxy instance.
        /// </summary>
        /// <param name="localIPAddress">Local IP address to bind to.</param>
        /// <param name="localPort">Local port to listen on.</param>
        /// <param name="localEnableSsl">Whether the local server supports TLS/SSL.</param>
        /// <param name="remoteServerHostName">Remote server hostname to forward all POP3 messages to.</param>
        /// <param name="remoteServerPort">Remote server port to connect to.</param>
        /// <param name="remoteServerEnableSsl">Whether the remote POP3 server requires TLS/SSL.</param>
        /// <param name="remoteServerCredential">(Optional) Credentials to be used for all connections to the remote POP3 server.  When set, this overrides any credentials passed locally.</param>
        /// <param name="logFile">File where event logs and exception information will be written.</param>
        public async Task StartProxy(IPAddress localIPAddress, int localPort, bool localEnableSsl, string remoteServerHostName, int remoteServerPort, bool remoteServerEnableSsl, NetworkCredential remoteServerCredential, string logFile)
        {
            Started = true;
            X509Certificate serverCertificate = null;

            // Generate a unique session ID for logging.
            SessionId = Guid.NewGuid().ToString();
            ConnectionId = 0;

            if (!string.IsNullOrEmpty(logFile))
            {
                // If the log file location doesn't contain a directory, make it relative to where the service lives.
                if (!logFile.Contains("\\"))
                    logFile = AppDomain.CurrentDomain.BaseDirectory + "\\" + logFile;

                LogWriter = new StreamWriter(logFile, true, Encoding.UTF8, Constants.BUFFERSIZE);
                LogWriter.AutoFlush = true;
            }

            // If local SSL is supported via STARTTLS, ensure we have a valid server certificate.
            if (localEnableSsl)
            {
                string fqdn = Functions.FQDN();
                serverCertificate = CertHelper.GetCertificateBySubjectName(StoreLocation.LocalMachine, fqdn);
                // In case the service as running as the current user, check the Current User certificate store as well.
                if (serverCertificate == null)
                    serverCertificate = CertHelper.GetCertificateBySubjectName(StoreLocation.CurrentUser, fqdn);

                // If no certificate was found, generate a self-signed certificate.
                if (serverCertificate == null)
                {
                    await ProxyFunctions.LogAsync(LogWriter, SessionId, "No signing certificate found, so generating new certificate.");

                    List<string> oids = new List<string>();
                    oids.Add("1.3.6.1.5.5.7.3.1");    // Server Authentication.

                    // Generate the certificate with a duration of 10 years, 4096-bits, and a key usage of server authentication.
                    serverCertificate = CertHelper.CreateSelfSignedCertificate(fqdn, fqdn, true, 4096, 10, oids);

                    StringBuilder logBuilder = new StringBuilder();
                    logBuilder.Append("Importing certificate with Serial Number {");
                    foreach (byte snByte in serverCertificate.GetSerialNumber())
                        logBuilder.Append(((int)snByte).ToString());
                    logBuilder.Append("}.");
                    await ProxyFunctions.LogAsync(LogWriter, SessionId, logBuilder.ToString());
                }
            }

            // Start listening on the specified port and IP address.
            Listener = new TcpListener(localIPAddress, localPort);
            Listener.Start();

            await ProxyFunctions.LogAsync(LogWriter, SessionId, "Starting to listen on address {" + localIPAddress.ToString() + "}, port {" + localPort + "}.");

            // Accept client requests, forking each into its own thread.
            while (Started)
            {
                TcpClient client = Listener.AcceptTcpClient();

                // Prepare the arguments for our new thread.
                Pop3ProxyConnectionArguments arguments = new Pop3ProxyConnectionArguments();
                arguments.TcpClient = client;
                arguments.Certificate = serverCertificate;
                arguments.LocalIpAddress = localIPAddress;
                arguments.LocalPort = localPort;
                arguments.LocalEnableSsl = localEnableSsl;
                arguments.RemoteServerHostName = remoteServerHostName;
                arguments.RemoteServerPort = remoteServerPort;
                arguments.RemoteServerEnableSsl = remoteServerEnableSsl;
                arguments.RemoteServerCredential = remoteServerCredential;

                // Increment the connection counter;
                arguments.ConnectionId = (unchecked(++ConnectionId)).ToString();

                // Fork the thread and continue listening for new connections.
                Thread t = new Thread(new ParameterizedThreadStart(ProcessConnection));
                t.Start(arguments);
            }
        }

        /// <summary>
        /// Stop the POP3 proxy and close all existing connections.
        /// </summary>
        public async void StopProxy()
        {
            await ProxyFunctions.LogAsync(LogWriter, SessionId, "Stopping service.");

            Started = false;

            if (Listener != null)
                Listener.Stop();
        }

        /// <summary>
        /// Start all POP3 proxy instances from the specified settings file.
        /// </summary>
        /// <param name="fileName">File containing the POP3 proxy settings.</param>
        public static List<Pop3Proxy> StartProxiesFromSettingsFile(string fileName)
        {
            List<Pop3Proxy> pop3Proxies = new List<Pop3Proxy>();

            if (File.Exists(fileName))
            {
                try
                {
                    XPathDocument document = new XPathDocument(fileName);
                    XPathNavigator navigator = document.CreateNavigator();

                    int pop3ServiceCount = ProxyFunctions.GetXmlIntValue(navigator, "/Settings/IMAP/ServiceCount");
                    for (int i = 1; i <= pop3ServiceCount; i++)
                    {
                        Pop3ProxyArguments arguments = new Pop3ProxyArguments();
                        string localIpAddress = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/LocalIPAddress").ToUpper();
                        switch (localIpAddress)
                        {
                            // Treat blank values as "Any".
                            case "":
                            case "ANY":
                                arguments.LocalIpAddress = IPAddress.Any;
                                break;
                            case "BROADCAST":
                                arguments.LocalIpAddress = IPAddress.Broadcast;
                                break;
                            case "IPV6ANY":
                                arguments.LocalIpAddress = IPAddress.IPv6Any;
                                break;
                            case "IPV6LOOPBACK":
                                arguments.LocalIpAddress = IPAddress.IPv6Loopback;
                                break;
                            case "LOOPBACK":
                                arguments.LocalIpAddress = IPAddress.Loopback;
                                break;
                            default:
                                // Try to parse the local IP address.  If unable to, proceed to the next service instance.
                                if (!IPAddress.TryParse(localIpAddress, out arguments.LocalIpAddress))
                                    continue;
                                break;
                        }

                        arguments.LocalPort = ProxyFunctions.GetXmlIntValue(navigator, "/Settings/POP3/Service" + i + "/LocalPort");
                        // If the port is invalid, proceed to the next service instance.
                        if (arguments.LocalPort < 1)
                            continue;

                        arguments.LocalEnableSsl = ProxyFunctions.GetXmlBoolValue(navigator, "/Settings/POP3/Service" + i + "/LocalEnableSSL");

                        arguments.RemoteServerHostName = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/RemoteServerHostName");
                        // If the host name is invalid, proceed to the next service instance.
                        if (string.IsNullOrEmpty(arguments.RemoteServerHostName))
                            continue;

                        arguments.RemoteServerPort = ProxyFunctions.GetXmlIntValue(navigator, "/Settings/POP3/Service" + i + "/RemoteServerPort");
                        // If the port is invalid, proceed to the next service instance.
                        if (arguments.RemoteServerPort < 1)
                            continue;

                        arguments.RemoteServerEnableSsl = ProxyFunctions.GetXmlBoolValue(navigator, "/Settings/POP3/Service" + i + "/RemoteServerEnableSSL");

                        string remoteServerUsername = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/RemoteServerUsername");
                        if (!string.IsNullOrEmpty(remoteServerUsername))
                        {
                            arguments.RemoteServerCredential = new NetworkCredential();
                            arguments.RemoteServerCredential.UserName = remoteServerUsername;
                            arguments.RemoteServerCredential.Password = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/RemoteServerPassword");
                        }

                        string certificateLocationValue = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/Certificate/Location");
                        StoreLocation certificateLocation = StoreLocation.LocalMachine;
                        if (certificateLocationValue.ToUpper() == "CURRENTUSER")
                            certificateLocation = StoreLocation.CurrentUser;

                        // Try to load the signing certificate based on its serial number first, then fallback to its subject name.
                        string certificateValue = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/Certificate/SerialNumber");
                        if (!string.IsNullOrEmpty(certificateValue))
                            arguments.Certificate = CertHelper.GetCertificateBySerialNumber(certificateLocation, certificateValue);
                        else
                        {
                            certificateValue = ProxyFunctions.GetXmlStringValue(navigator, "/Settings/POP3/Service" + i + "/Certificate/SubjectName");
                            if (!string.IsNullOrEmpty(certificateValue))
                                arguments.Certificate = CertHelper.GetCertificateBySubjectName(certificateLocation, certificateValue);
                        }

                        arguments.LogFile = ProxyFunctions.GetXmlStringValue(navigator, "Settings/POP3/Service" + i + "/LogFile");

                        // Remember the proxy in order to close it when the service stops.
                        arguments.Proxy = new Pop3Proxy();
                        pop3Proxies.Add(arguments.Proxy);

                        Thread proxyThread = new Thread(new ParameterizedThreadStart(StartPop3Proxy));
                        proxyThread.Start(arguments);
                    }
                }
                catch (Exception)
                {
                    // Ignore errors if the XML settings file is malformed.
                }
            }

            return pop3Proxies;
        }
        #endregion Public Methods

        #region Private Methods
        /// <summary>
        /// Handle an incoming POP3 connection, from connection to completion.
        /// </summary>
        /// <param name="parameters">Pop3ProxyConnectionArguments object containing all parameters for this connection.</param>
        private async void ProcessConnection(object parameters)
        {
            // Cast the passed-in parameters back to their original objects.
            Pop3ProxyConnectionArguments arguments = (Pop3ProxyConnectionArguments)parameters;

            TcpClient client = arguments.TcpClient;
            Stream clientStream = client.GetStream();

            // Capture the client's IP information.
            PropertyInfo pi = clientStream.GetType().GetProperty("Socket", BindingFlags.NonPublic | BindingFlags.Instance);
            string ip = ((Socket)pi.GetValue(clientStream, null)).RemoteEndPoint.ToString();
            if (ip.IndexOf(":") > -1)
                ip = ip.Substring(0, ip.IndexOf(":"));

            await ProxyFunctions.LogAsync(LogWriter, SessionId, arguments.ConnectionId, "New connection established from {" + ip + "}.");

            // If supported, upgrade the session's security through a TLS handshake.
            if (arguments.LocalEnableSsl)
            {
                await ProxyFunctions.LogAsync(LogWriter, SessionId, arguments.ConnectionId, "Starting local TLS/SSL protection for {" + ip + "}.");
                clientStream = new SslStream(clientStream);
                ((SslStream)clientStream).AuthenticateAsServer(arguments.Certificate);
            }

            // Connect to the remote server.
            TcpClient remoteServerClient = new TcpClient(arguments.RemoteServerHostName, arguments.RemoteServerPort);
            Stream remoteServerStream = remoteServerClient.GetStream();

            // If supported, upgrade the session's security through a TLS handshake.
            if (arguments.RemoteServerEnableSsl)
            {
                await ProxyFunctions.LogAsync(LogWriter, SessionId, arguments.ConnectionId, "Starting remote TLS/SSL protection with {" + arguments.RemoteServerHostName + "}.");
                remoteServerStream = new SslStream(remoteServerStream);
                ((SslStream)remoteServerStream).AuthenticateAsClient(arguments.RemoteServerHostName);
            }

            // Relay server data to the client.
            TransmitArguments remoteServerToClientArguments = new TransmitArguments();
            remoteServerToClientArguments.ClientStream = remoteServerStream;
            remoteServerToClientArguments.RemoteServerStream = clientStream;
            remoteServerToClientArguments.IsClient = true;
            remoteServerToClientArguments.ConnectionId = ConnectionId.ToString();
            Thread remoteServerToClientThread = new Thread(new ParameterizedThreadStart(RelayData));
            remoteServerToClientThread.Start(remoteServerToClientArguments);

            // Relay client data to the remote server.
            TransmitArguments clientToRemoteServerArguments = new TransmitArguments();
            clientToRemoteServerArguments.ClientStream = clientStream;
            clientToRemoteServerArguments.RemoteServerStream = remoteServerStream;
            clientToRemoteServerArguments.IsClient = false;
            clientToRemoteServerArguments.ConnectionId = ConnectionId.ToString();
            Thread clientToRemoteServerThread = new Thread(new ParameterizedThreadStart(RelayData));
            clientToRemoteServerThread.Start(clientToRemoteServerArguments);
        }

        /// <summary>
        /// Relay data read from one connection to another.
        /// </summary>
        /// <param name="o">A TransmitArguments object containing local and remote server parameters.</param>
        private async void RelayData(object o)
        {
            // Cast the passed-in parameters back to their original objects.
            TransmitArguments arguments = (TransmitArguments)o;
            Stream clientStream = arguments.ClientStream;
            Stream remoteServerStream = arguments.RemoteServerStream;

            // A byte array to streamline bit shuffling.
            byte[] buffer = new byte[Constants.BUFFERSIZE];

            // Placeholder variables to track the current message being transmitted.
            StringBuilder messageBuilder = new StringBuilder();

            // The overall number of bytes transmitted on this connection.
            ulong bytesTransmitted = 0;

            // Delay period between reads.
            int delay = 250;

            try
            {
                while (Started)
                {
                    // Read data from the source and send it to its destination.
                    int bytesRead = await clientStream.ReadAsync(buffer, 0, Constants.BUFFERSIZE);

                    if (bytesRead > 0)
                    {
                        await remoteServerStream.WriteAsync(buffer, 0, bytesRead);

                        if (delay > 250)
                            delay -= 250;
                        bytesTransmitted += (ulong)bytesRead;

                        // Cast the bytes received to a string.
                        string stringRead = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(stringRead);

                        if (!arguments.IsClient)
                        {
                            string[] commandParts = stringRead.Substring(0, stringRead.Length - 2).Split(new char[] { ' ' }, 2);
                            await ProxyFunctions.LogAsync(LogWriter, SessionId, arguments.ConnectionId.ToString(), "Command {" + commandParts[0] + "} processed.");
                        }

                        // If we see a period between two linebreaks, treat it as the end of a message.
                        int endPos = stringRead.IndexOf("\r\n.\r\n");
                        if (endPos > -1)
                        {
                            string message = messageBuilder.ToString();
                            endPos = message.IndexOf("\r\n.\r\n");

                            int lastOkPos = message.LastIndexOf("+OK", endPos);
                            if (lastOkPos > -1)
                            {
                                if (message.IndexOf("application/x-pkcs7-signature") > -1 || message.IndexOf("application/pkcs7-mime") > -1)
                                {
                                    Thread processThread = new Thread(new ParameterizedThreadStart(ProcessMessage));
                                    ProcessMessageArguments processMessageArguments = new ProcessMessageArguments();
                                    processMessageArguments.MessageText = message;
                                    processMessageArguments.ConnectionId = ConnectionId.ToString();
                                    processThread.Start(processMessageArguments);
                                }
                                messageBuilder.Remove(0, endPos + 5);
                            }
                            else
                                messageBuilder.Clear();
                        }
                    }
                    else
                        Thread.Sleep(delay == 5000 ? 5000 : delay += 250);
                }
            }
            catch (IOException)
            {
                // Ignore either stream being closed.
            }
            catch (ObjectDisposedException)
            {
                // Ignore either stream being closed.
            }
            catch (Exception ex)
            {
                // Log other exceptions.
                ProxyFunctions.Log(LogWriter, SessionId, "Exception: " + ex.ToString());
            }
            finally
            {
                // If sending to the local client, log the connection being closed.
                if (arguments.IsClient)
                    ProxyFunctions.Log(LogWriter, SessionId, arguments.ConnectionId, "Connection closed after transmitting {" + bytesTransmitted + "} bytes.");

                if (clientStream != null)
                    clientStream.Dispose();
                if (remoteServerStream != null)
                    remoteServerStream.Dispose();
            }
        }

        /// <summary>
        /// Process a transmitted message to import any signing certificates for subsequent S/MIME encryption.
        /// </summary>
        /// <param name="o">A ProcessMessageArguments object containing message parameters.</param>
        private void ProcessMessage(object o)
        {
            ProcessMessageArguments arguments = (ProcessMessageArguments)o;

            // Only parse the message if it contains a known S/MIME content type.
            string canonicalMessageText = arguments.MessageText.ToLower();
            if (canonicalMessageText.IndexOf("application/x-pkcs7-signature") > -1 || canonicalMessageText.IndexOf("application/pkcs7-mime") > -1)
            {
                // Parse the message.
                ReadOnlyMailMessage message = new ReadOnlyMailMessage(arguments.MessageText);

                // If the message contains a signing certificate that we haven't processed on this session, import it.
                if (message.SmimeSigningCertificate != null && !SmimeCertificatesReceived.Contains(message.SmimeSigningCertificate))
                {
                    StringBuilder logBuilder = new StringBuilder();
                    logBuilder.Append("Importing certificate with Serial Number {");
                    foreach (byte snByte in message.SmimeSigningCertificate.GetSerialNumber())
                        logBuilder.Append(((int)snByte).ToString());
                    logBuilder.Append("}.");

                    // Import the certificate to the Local Machine store.
                    ProxyFunctions.Log(LogWriter, SessionId, arguments.ConnectionId, logBuilder.ToString());
                    CertHelper.InstallWindowsCertificate(message.SmimeSigningCertificate, StoreLocation.LocalMachine);
                    ProxyFunctions.Log(LogWriter, SessionId, arguments.ConnectionId, "Certificate imported.");

                    // Remember this ceriticate to avoid importing it again this session.
                    SmimeCertificatesReceived.Add(message.SmimeSigningCertificate);
                }
            }
        }

        /// <summary>
        /// Start an individual POP3 proxy on its own thread.
        /// </summary>
        /// <param name="parameters">Pop3ProxyArguments object containing all parameters for this connection.</param>
        private static async void StartPop3Proxy(object parameters)
        {
            Pop3ProxyArguments arguments = (Pop3ProxyArguments)parameters;

            // Start the proxy using passed-in settings.
            await arguments.Proxy.StartProxy(arguments.LocalIpAddress, arguments.LocalPort, arguments.LocalEnableSsl, arguments.RemoteServerHostName, arguments.RemoteServerPort, arguments.RemoteServerEnableSsl, arguments.RemoteServerCredential, arguments.LogFile);
        }
        #endregion Private Methods
    }
}