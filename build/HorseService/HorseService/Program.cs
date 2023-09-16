﻿using Horse.Jockey;
using Horse.Jockey.Models.User;
using Horse.Messaging.Data;
using Horse.Messaging.Protocol;
using Horse.Messaging.Server;
using Horse.Messaging.Server.Cluster;
using Horse.Messaging.Server.Queues;
using Horse.Messaging.Server.Queues.Delivery;
using Horse.Server;
using HorseService;

// Load Options

string optionsFilename = "/etc/horse/options.json";
AppOptions appOptions = null;

if (!Directory.Exists("/etc/horse"))
    Directory.CreateDirectory("/etc/horse");

if (File.Exists(optionsFilename))
{
    string json = File.ReadAllText(optionsFilename);
    appOptions = Newtonsoft.Json.JsonConvert.DeserializeObject<AppOptions>(json);
}
else
{
    appOptions = new AppOptions();
}

appOptions.Port = 2626;
appOptions.JockeyPort = 2627;

string datapath = Environment.GetEnvironmentVariable("HORSE_DATA_PATH");
string clusterType = Environment.GetEnvironmentVariable("HORSE_CLUSTER_TYPE");
string nodeName = Environment.GetEnvironmentVariable("HORSE_NODE_NAME");
string otherNodes = Environment.GetEnvironmentVariable("HORSE_NODES");
string clusterSecret = Environment.GetEnvironmentVariable("HORSE_CLUSTER_SECRET");
string jockey = Environment.GetEnvironmentVariable("HORSE_JOCKEY");
string username = Environment.GetEnvironmentVariable("HORSE_JOCKEY_USERNAME");
string password = Environment.GetEnvironmentVariable("HORSE_JOCKEY_PASSWORD");

if (!string.IsNullOrEmpty(username))
    appOptions.JockeyUsername = username;

if (!string.IsNullOrEmpty(password))
    appOptions.JockeyPassword = password;

if (!string.IsNullOrEmpty(datapath))
    appOptions.DataPath = datapath;

// Initialize Server
HorseServer server = new HorseServer();

HorseRider rider = HorseRiderBuilder.Create()
    .ConfigureOptions(o => { o.DataPath = appOptions.DataPath; })
    .ConfigureChannels(c =>
    {
        c.Options.AutoDestroy = true;
        c.Options.AutoChannelCreation = true;
    })
    .ConfigureCache(c =>
    {
        c.Options.DefaultDuration = TimeSpan.FromMinutes(15);
        c.Options.MinimumDuration = TimeSpan.FromHours(6);
    })
    .ConfigureQueues(c =>
    {
        c.Options.AutoQueueCreation = true;
        c.UsePersistentQueues(d => { d.UseAutoFlush(TimeSpan.FromMilliseconds(50)); },
            q =>
            {
                q.Options.AutoDestroy = QueueDestroy.Disabled;
                q.Options.CommitWhen = CommitWhen.AfterReceived;
                q.Options.PutBack = PutBackDecision.Regular;
                q.Options.Acknowledge = QueueAckDecision.WaitForAcknowledge;
                q.Options.PutBackDelay = 5000;
            });
    })
    .Build();


// Clustering options
if (!string.IsNullOrEmpty(otherNodes))
{
    rider.Cluster.Options.Mode = !string.IsNullOrEmpty(clusterType) && clusterType.Equals("Reliable", StringComparison.InvariantCultureIgnoreCase)
        ? ClusterMode.Reliable
        : ClusterMode.Scaled;

    rider.Cluster.Options.Name = nodeName;
    rider.Cluster.Options.NodeHost = "horse://localhost:2628";
    rider.Cluster.Options.PublicHost = $"horse://localhost:{appOptions.Port}";
    rider.Cluster.Options.SharedSecret = clusterSecret;

    foreach (string otherNode in otherNodes.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        rider.Cluster.Options.Nodes.Add(new NodeInfo
        {
            Name = otherNode,
            Host = $"horse://{otherNode}:2628",
            PublicHost = $"horse://{otherNode}:2626"
        });
    }
}

// Add Jockey
bool skipJockey = !string.IsNullOrEmpty(jockey) && jockey == "0";
if (!skipJockey)
{
    rider.AddJockey(o =>
    {
        o.CustomSecret = $"{Guid.NewGuid()}-{Guid.NewGuid()}-{Guid.NewGuid()}";

        o.Port = appOptions.JockeyPort;
        o.AuthAsync = login =>
        {
            if (login.Username == appOptions.JockeyUsername && login.Password == appOptions.JockeyPassword)
                return Task.FromResult(new UserInfo {Id = "*", Name = "Admin"});

            return Task.FromResult<UserInfo>(null);
        };
    });
}


// Run
server.UseRider(rider);
server.Run(appOptions.Port);