﻿namespace Runner
{
    using System;
    using System.Configuration;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Transactions;
    using NServiceBus;
    using NServiceBus.Features;
    using NServiceBus.Installation.Environments;
    using NServiceBus.Persistence.NHibernate;
    using Saga;

    internal class Program
    {
        static void Main(string[] args)
        {
            var numberOfThreads = int.Parse(args[0]);
            var volatileMode = (args[4].ToLower() == "volatile");
            var suppressDTC = (args[4].ToLower() == "suppressdtc");
            var twoPhaseCommit = (args[4].ToLower() == "twophasecommit");
            var outbox = (args[4].ToLower() == "outbox");
            var saga = (args[5].ToLower() == "sagamessages");
            var publish = (args[5].ToLower() == "publishmessages");
            var concurrency = int.Parse(args[7]);

            TransportConfigOverride.MaximumConcurrencyLevel = numberOfThreads;

            var numberOfMessages = int.Parse(args[1]);

            var endpointName = "PerformanceTest";

            if (volatileMode)
            {
                endpointName += ".Volatile";
            }

            if (suppressDTC)
            {
                endpointName += ".SuppressDTC";
            }

            if (outbox)
            {
                endpointName += ".outbox";
            }

            var config = Configure.With()
                .DefineEndpointName(endpointName)
                .DefaultBuilder()
                .UseTransport<Msmq>();

            switch (args[2].ToLower())
            {
                case "xml":
                    Configure.Serialization.Xml();
                    break;

                case "json":
                    Configure.Serialization.Json();
                    break;

                case "bson":
                    Configure.Serialization.Bson();
                    break;

                case "bin":
                    Configure.Serialization.Binary();
                    break;

                default:
                    throw new InvalidOperationException("Illegal serialization format " + args[2]);
            }

            Configure.Features.Disable<Audit>();

            //Configure.Instance.UnicastBus().IsolationLevel(IsolationLevel.Snapshot);
            //Console.Out.WriteLine("Snapshot");

            NHibernateSettingRetriever.ConnectionStrings = () => new ConnectionStringSettingsCollection
                {
                    new ConnectionStringSettings("NServiceBus/Persistence", SqlServerConnectionString)
                };

            if (saga)
            {
                Configure.Features.Enable<Sagas>();

                config.UseNHibernateSagaPersister();
            }
            else if (publish)
            {
                
                config.UseNHibernateSubscriptionPersister(TimeSpan.FromSeconds(1));
            }

            if (suppressDTC)
            {
                Configure.Transactions.Advanced(settings => settings.DisableDistributedTransactions());
            }

            if (outbox)
            {
                Configure.Transactions.Advanced(settings => settings.DisableDistributedTransactions().DoNotWrapHandlersExecutionInATransactionScope());
                config.UseNHibernateOutbox();
            }

            using (var startableBus = config.InMemoryFaultManagement().UnicastBus().CreateBus())
            {
                Configure.Instance.ForInstallationOn<Windows>().Install();

                if (saga)
                {
                    SeedSagaMessages(numberOfMessages, endpointName, concurrency);
                }
                else if (publish)
                {
                    Statistics.PublishTimeNoTx = PublishEvents(numberOfMessages/2, numberOfThreads, false);
                    Statistics.PublishTimeWithTx = PublishEvents(numberOfMessages/2, numberOfThreads, !outbox);
                }
                else
                {
                    Statistics.SendTimeNoTx = SeedInputQueue(numberOfMessages/2, endpointName, numberOfThreads, false, twoPhaseCommit);
                    Statistics.SendTimeWithTx = SeedInputQueue(numberOfMessages/2, endpointName, numberOfThreads, !outbox, twoPhaseCommit);
                }

                Statistics.StartTime = DateTime.Now;

                startableBus.Start();

                while (Interlocked.Read(ref Statistics.NumberOfMessages) < numberOfMessages)
                {
                    Thread.Sleep(1000);
                }

                DumpSetting(args);
                Statistics.Dump();
            }
        }


        static void DumpSetting(string[] args)
        {
            Console.Out.WriteLine("---------------- Settings ----------------");
            Console.Out.WriteLine("Threads: {0}, Serialization: {1}, Transport: {2}, Messagemode: {3}",
                args[0],
                args[2],
                args[3],
                args[5]);
        }

        static void SeedSagaMessages(int numberOfMessages, string inputQueue, int concurrency)
        {
            var bus = Configure.Instance.Builder.Build<IBus>();

            for (var i = 0; i < numberOfMessages/concurrency; i++)
            {
                for (var j = 0; j < concurrency; j++)
                {
                    bus.Send(inputQueue, new StartSagaMessage
                    {
                        Id = i
                    });
                }
            }
        }

        static TimeSpan SeedInputQueue(int numberOfMessages, string inputQueue, int numberOfThreads, bool createTransaction, bool twoPhaseCommit)
        {
            var sw = new Stopwatch();
            var bus = Configure.Instance.Builder.Build<IBus>();

            sw.Start();
            Parallel.For(
                0,
                numberOfMessages,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = numberOfThreads
                },
                x =>
                {
                    var message = CreateMessage();
                    message.TwoPhaseCommit = twoPhaseCommit;
                    message.Id = x;

                    if (createTransaction)
                    {
                        using (var tx = new TransactionScope())
                        {
                            bus.Send(inputQueue, message);
                            tx.Complete();
                        }
                    }
                    else
                    {
                        bus.Send(inputQueue, message);
                    }
                });
            sw.Stop();

            return sw.Elapsed;
        }

        static TimeSpan PublishEvents(int numberOfMessages, int numberOfThreads, bool createTransaction)
        {
            var sw = new Stopwatch();
            var bus = Configure.Instance.Builder.Build<IBus>();

            sw.Start();
            Parallel.For(
                0,
                numberOfMessages,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = numberOfThreads
                },
                x =>
                {
                    if (createTransaction)
                    {
                        using (var tx = new TransactionScope())
                        {
                            bus.Publish<TestEvent>();
                            tx.Complete();
                        }
                    }
                    else
                    {
                        bus.Publish<TestEvent>();
                    }
                    Interlocked.Increment(ref Statistics.NumberOfMessages);
                });
            sw.Stop();

            return sw.Elapsed;
        }

        static MessageBase CreateMessage()
        {
            return new TestMessage();
        }

        static string SqlServerConnectionString = @"Server=localhost\sqlexpress;Database=nservicebus;Trusted_Connection=True;";
    }
}