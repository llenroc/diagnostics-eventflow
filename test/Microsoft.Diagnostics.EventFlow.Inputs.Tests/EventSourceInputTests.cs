﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using Moq;
using Xunit;

#if !NETSTANDARD1_6
using System.Collections.ObjectModel;
using System.Threading.Tasks;
#endif

using Microsoft.Diagnostics.EventFlow.Inputs;
using Microsoft.Diagnostics.EventFlow.Configuration;

namespace Microsoft.Diagnostics.EventFlow.Inputs.Tests
{
#if !NET451

    public class EventSourceInputTests
    {
        [Fact]
        public void HandlesDuplicatePropertyNames()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderName = "EventSourceInput-TestEventSource"
            });
            var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object);
            eventSourceInput.Activate();

            var observer = new Mock<IObserver<EventData>>();
            using (eventSourceInput.Subscribe(observer.Object))
            {
                EventSourceInputTestSource.Log.Tricky(7, "TrickyEvent", "Actual message");

                observer.Verify(s => s.OnNext(It.Is<EventData>(data =>
                       data.Payload["Message"].Equals("Manifest message")
                    && data.Payload["EventId"].Equals(1)
                    && data.Payload["EventName"].Equals("Tricky")
                    && data.Payload[data.Payload.Keys.First(key => key.StartsWith("Message") && key != "Message")].Equals("Actual message")
                    && data.Payload[data.Payload.Keys.First(key => key.StartsWith("EventId") && key != "EventId")].Equals(7)
                    && data.Payload[data.Payload.Keys.First(key => key.StartsWith("EventName") && key != "EventName")].Equals("TrickyEvent")
                )), Times.Exactly(1));

                healthReporterMock.Verify(o => o.ReportWarning(
                        It.Is<string>(s => s.Contains("already exist in the event payload")),
                        It.Is<string>(s => s == nameof(EventSourceInput))),
                    Times.Exactly(3));
            }
        }

        [Fact]
        public void CapturesEventsFromEventSourceExistingBeforeInputCreated()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderName = "EventSourceInput-TestEventSource"
            });

            // EventSourceInputTestSource has a static instance that exists before the input is created.
            // But it won't be actually hooked up to EventSource/EventListener infrastructure until an event is raised.
            EventSourceInputTestSource.Log.Message("ignored");

            var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object);
            eventSourceInput.Activate();

            var observer = new Mock<IObserver<EventData>>();
            using (eventSourceInput.Subscribe(observer.Object))
            {
                EventSourceInputTestSource.Log.Message("Hello!");

                observer.Verify(s => s.OnNext(It.Is<EventData>(data =>
                       data.Payload["Message"].Equals("Hello!")
                    && data.Payload["EventId"].Equals(2)
                    && data.Payload["EventName"].Equals("Message")
                )), Times.Exactly(1));

                healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            }
        }

        [Fact]
        public void CapturesEventsFromEventSourceCreatedAfterInputCreated()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderName = "EventSourceInput-OtherTestEventSource"
            });

            var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object);
            eventSourceInput.Activate();

            var observer = new Mock<IObserver<EventData>>();
            using (eventSourceInput.Subscribe(observer.Object))
            using (var eventSource = new EventSourceInputTestOtherSource())
            {
                eventSource.Message("Wow!");

                observer.Verify(s => s.OnNext(It.Is<EventData>(data =>
                       data.Payload["Message"].Equals("Wow!")
                    && data.Payload["EventId"].Equals(3)
                    && data.Payload["EventName"].Equals("Message")
                )), Times.Exactly(1));

                healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            }
        }

        // High-precision event timestamping is availabe on .NET 4.6+ and .NET Core 2.0+
#if NET46
        [Fact]
        public void MeasuresEventTimeWithHighResolution()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            // Ensure the EventSource is instantiated
            EventSourceInputTestSource.Log.Message("warmup");

            List<DateTimeOffset> eventTimes = new List<DateTimeOffset>();
            Action<EventWrittenEventArgs> eventHandler = e => eventTimes.Add(EventDataExtensions.ToEventData(e, healthReporterMock.Object, "context-unused").Timestamp);
            var hundredMicroseconds = Math.Round(Stopwatch.Frequency / 10000.0);
            using (var listener = new EventSourceInputTestListener(eventHandler))
            {
                for (int i=0; i < 8; i++)
                {
                    EventSourceInputTestSource.Log.Message(i.ToString());
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedTicks < hundredMicroseconds)
                    {
                        // Spin wait
                    }
                }
            }

            Assert.True(eventTimes.Distinct().Count() == 8, "Event timestamps should have less than 1 ms resolution and thus should all be different");
        }
#endif

        [Fact]
        public void CapturesEventsFromSourcesIdentifiedByNamePrefix()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Test"
            });

            var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object);
            eventSourceInput.Activate();

            var observer = new Mock<IObserver<EventData>>();
            using (eventSourceInput.Subscribe(observer.Object))
            {
                EventSourceInputTestSource.Log.Message("Hello!");

                observer.Verify(s => s.OnNext(It.Is<EventData>(data =>
                       data.Payload["Message"].Equals("Hello!")
                    && data.Payload["EventId"].Equals(2)
                    && data.Payload["EventName"].Equals("Message")
                )), Times.Exactly(1));

                healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            }
        }

        [Fact]
        public void OmitsEventsFromSourcesDisabledByNamePrefix()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Other"
            });

            var observer = new Mock<IObserver<EventData>>();
            using (var otherSource = new EventSourceInputTestOtherSource())
            {
                using (var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object))
                {
                    eventSourceInput.Activate();

                    using (eventSourceInput.Subscribe(observer.Object))
                    {
                        otherSource.Message("Hey!");

                        observer.Verify(s => s.OnNext(It.IsAny<EventData>()), Times.Exactly(1));

                        healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                        healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                    }
                }

                inputConfiguration.Add(new EventSourceConfiguration()
                {
                    DisabledProviderNamePrefix = "EventSourceInput-Other"
                });
                observer.ResetCalls();

                using (var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object))
                {
                    eventSourceInput.Activate();

                    using (eventSourceInput.Subscribe(observer.Object))
                    {
                        otherSource.Message("You!");

                        observer.Verify(s => s.OnNext(It.IsAny<EventData>()), Times.Exactly(0));  // Source disabled--should get zero events out of the input

                        healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                        healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                    }
                }
            }
        }

        [Fact]
        public void CannotEnableAndDisableBySingleConfigurationItem()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Other",
                DisabledProviderNamePrefix = "EventSourceInput-Test"
            });

            var observer = new Mock<IObserver<EventData>>();
            using (var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object))
            {
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.Is<string>(s => s == EventFlowContextIdentifiers.Configuration)), Times.Once());
            }
        }

        [Fact]
        public void CannotEnableByNameAndByPrefixBySingleConfigurationItem()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Other",
                ProviderName = "EventSourceInput-OtherTestEventSource"
            });

            var observer = new Mock<IObserver<EventData>>();
            using (var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object))
            {
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.Is<string>(s => s == EventFlowContextIdentifiers.Configuration)), Times.Once());
            }
        }

        [Fact]
        public void DisabledSourcesCannotSpecifyLevelOrKeywords()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                DisabledProviderNamePrefix = "EventSourceInput-Other",
                Level = EventLevel.Warning
            });
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                DisabledProviderNamePrefix = "EventSourceInput-Test",
                Keywords = (EventKeywords) 0x4
            });

            var observer = new Mock<IObserver<EventData>>();
            using (var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object))
            {
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.Is<string>(s => s == EventFlowContextIdentifiers.Configuration)), Times.Exactly(2));
            }
        }

        // Enabling events with different levels and keywords does not work on .NET Core 1.1. and 2.0
        // (following up with .NET team to see if there is something we can do about it)
#if NET46
        [Fact]
        public void CanEnableSameSourceWithDifferentLevelsAndKeywords()
        {
            var healthReporterMock = new Mock<IHealthReporter>();

            var inputConfiguration = new List<EventSourceConfiguration>();
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Test",
                Level = EventLevel.Warning,
                Keywords = EventSourceInputTestSource.Keywords.Important
            });
            inputConfiguration.Add(new EventSourceConfiguration()
            {
                ProviderNamePrefix = "EventSourceInput-Test",
                Level = EventLevel.Informational,
                Keywords = EventSourceInputTestSource.Keywords.Negligible
            });

            var eventSourceInput = new EventSourceInput(inputConfiguration, healthReporterMock.Object);
            eventSourceInput.Activate();

            var observer = new Mock<IObserver<EventData>>();
            using (eventSourceInput.Subscribe(observer.Object))
            {
                EventSourceInputTestSource.Log.Tricky(1, "Foo", "Bar");     // Not captured because it is only Level=Informational
                EventSourceInputTestSource.Log.Message("Hey!");             // Captured
                EventSourceInputTestSource.Log.DebugMessage("Yo!");         // Not captured because Level=Verbose

                observer.Verify(s => s.OnNext(It.IsAny<EventData>()), Times.Exactly(1));

                healthReporterMock.Verify(o => o.ReportWarning(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
                healthReporterMock.Verify(o => o.ReportProblem(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
            }
        }
#endif

        [EventSource(Name = "EventSourceInput-TestEventSource")]
        private class EventSourceInputTestSource : EventSource
        {
            public static EventSourceInputTestSource Log = new EventSourceInputTestSource();

            [Event(1, Level = EventLevel.Informational, Message ="Manifest message", Keywords = Keywords.Important)]
            public void Tricky(int EventId, string EventName, string Message)
            {
                WriteEvent(1, EventId, EventName, Message);
            }

            [Event(2, Level = EventLevel.Informational, Message ="{0}", Keywords = Keywords.Negligible)]
            public void Message(string message)
            {
                WriteEvent(2, message);
            }

            [Event(3, Level = EventLevel.Verbose, Message = "{0}", Keywords = Keywords.Negligible)]
            public void DebugMessage(string message)
            {
                WriteEvent(3, message);
            }

            public class Keywords
            {
                public const EventKeywords Important = (EventKeywords) 0x1;
                public const EventKeywords Negligible = (EventKeywords) 0x2;
            }
        }

        [EventSource(Name = "EventSourceInput-OtherTestEventSource")]
        private class EventSourceInputTestOtherSource: EventSource
        {
            [Event(3, Level = EventLevel.Informational, Message = "{0}")]
            public void Message(string message)
            {
                WriteEvent(3, message);
            }
        }

        private class EventSourceInputTestListener : EventListener
        {
            private Action<EventWrittenEventArgs> eventHandler;

            public EventSourceInputTestListener(Action<EventWrittenEventArgs> eventHandler = null)
            {
                this.eventHandler = eventHandler;
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                if (this.eventHandler != null)
                {
                    this.eventHandler(eventData);
                }
            }

            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source is EventSourceInputTestSource || source is EventSourceInputTestOtherSource)
                {
                    this.EnableEvents(source, EventLevel.Verbose);
                }
            }
        }
    }

#endif
    }
