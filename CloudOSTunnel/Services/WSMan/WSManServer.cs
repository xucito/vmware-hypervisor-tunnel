﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Xml;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

using CloudOSTunnel.Clients;

namespace CloudOSTunnel.Services.WSMan
{
    public class WSManServer : ITunnel, IWSManLogging, IDisposable
    {
        #region Configuration
        private const bool useSsl = true;
        private const string certPath = "Certificates/CloudOSTunnel.pfx";
        private const string certPassword = "CloudOSTunnel";
        #endregion Configuration

        // VMware client to access vCenter
        public VMWareClient Client { get; private set; }

        // WSMan runtime dictionary: command id as key
        private Dictionary<string, WSManServerRuntime> runtime;
        // Signal stopped server (used by WsmanServerManager for cleanup)
        public bool IsStopped { get; private set; }

        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger<WSManServer> logger;
        private IWebHost host;
        private int port;
        private string proto;

        #region ITunnel
        public string Hostname { get { return Client.HostName; } }
        public string Reference { get { return Client.MoRef; } }

        public void Shutdown()
        {
            if (IsStopped)
            {
                LogWarning("Already stopped");
            }
            else
            {
                // Stop web host
                host.StopAsync().Wait();

                lock (this)
                {
                    // Indicate server stopped for removal
                    IsStopped = true;
                }

                // Fast dispose to avoid processing queued requests
                Dispose();
            }
        }

        public void StartListening(int port)
        {
            this.port = port;
            this.proto = useSsl ? "https" : "http";

            // Create web host
            this.host = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    // Inject WSManHandler object for web host to use
                    services.AddSingleton(
                        new WSManHandler(loggerFactory, this, proto, port, Client.VmUser, Client.VmPassword));
                })
                .UseKestrel(options =>
                {
                    options.ListenAnyIP((int)port, listenOptions =>
                    {
                        if (useSsl)
                        {
                            LogInformation(string.Format("Configuring https web host on port {0}", port));
                            listenOptions.UseHttps(certPath, certPassword);
                        }
                        else
                        {
                            LogInformation(string.Format("Configuring http web host on port {0}", port));
                        }
                    });
                })
                .UseStartup<WSManStartup>()
                .Build();

            if (IsStopped)
            {
                throw new WSManException("Unable to start a wsman server again after it has been stopped");
            }

            // Indicate server started
            IsStopped = false;

            // Start web host
            host.Start();
        }
        #endregion ITunnel

        public WSManServer(ILoggerFactory loggerFactory, VMWareClient client)
        {
            this.logger = loggerFactory.CreateLogger<WSManServer>();
            this.loggerFactory = loggerFactory;
            this.Client = client;

            this.IsStopped = false;
            this.runtime = new Dictionary<string, WSManServerRuntime>();
        }

        #region Logging
        // ID for logging e.g. https://*:59860 (for server level logging)
        public string Id
        {
            get { return string.Format("{0}://{1}:{2}", proto, "*", port); }
        }

        public void LogInformation(string msg)
        {
            logger.LogInformation(string.Format("{0} {1}", Id, msg));
        }

        public void LogDebug(string msg)
        {
            logger.LogDebug(string.Format("{0} {1}", Id, msg));
        }

        public void LogWarning(string msg)
        {
            logger.LogWarning(string.Format("{0} {1}", Id, msg));
        }

        public void LogError(string msg)
        {
            logger.LogError(string.Format("{0} {1}", Id, msg));
        }
        #endregion Logging

        /// <summary>
        /// Handle Wsman protocol request 
        /// </summary>
        /// <param name="xml">XML request</param>
        /// <returns>Wsman response</returns>
        public async Task<string> HandleWsman(XmlDocument xml)
        {
            string response;
            // <w:ResourceURI mustUnderstand="true">http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd</w:ResourceURI>
            string resourceUri = xml.GetElementsByTagName("w:ResourceURI").Item(0).InnerText;
            string action = xml.GetElementsByTagName("a:Action").Item(0).InnerText;

            if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create")
            {
                response = WSManServerRuntime.HandleCreateShellAction(xml, Client.VmUser, this);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command")
            {
                // Generate a command id and create runtime
                string commandId = "" + Guid.NewGuid();
                var wsmanRuntime = new WSManServerRuntime(loggerFactory, Client, port, commandId, Id);
                runtime.Add(commandId, wsmanRuntime);

                response = wsmanRuntime.HandleExecuteCommandAction(xml);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Send")
            {
                string commandId = xml.GetElementsByTagName("rsp:Stream").Item(0).Attributes["CommandId"].Value;
                response = await runtime[commandId].HandleSendInputAction(xml, port);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive")
            {
                string commandId = xml.GetElementsByTagName("rsp:DesiredStream").Item(0).Attributes["CommandId"].Value;
                response = await runtime[commandId].HandleReceiveAction(xml, commandId);
            }
            else if (action == "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal")
            {
                string commandId = xml.GetElementsByTagName("rsp:Signal").Item(0).Attributes["CommandId"].Value;
                // Delete runtime for the command id
                response = runtime[commandId].HandleSignalAction(xml);
                runtime.Remove(commandId);
            }
            else if (action == "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete")
            {
                response = WSManServerRuntime.HandleDeleteShellAction(xml, this);
            }
            else
            {
                throw new InvalidOperationException(string.Format("Unknown wsman action {0}", action));
            }

            return response;
        }

        /// <summary>
        /// Dispose WsmanServer resources
        /// </summary>
        public void Dispose()
        {
            Client.Dispose();
            host.Dispose();

            // Delete all runtime commands
            runtime.Clear();
        }
    }
}
