using System.Net;
using IGoLibrary.Application.Exceptions;
using IGoLibrary.Application.Services;
using IGoLibrary.Application.State;
using IGoLibrary.Domain.Enums;
using IGoLibrary.Domain.Models;
using IGoLibrary.Infrastructure.Api;
using RestSharp;

namespace IGoLibrary.Tests;

public sealed class TraceIntApiClientTests
{
    [Fact]
    public async Task GetCurrentUserNicknameAsync_ReturnsTrimmedWechatNickname()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {"data":{"userAuth":{"currentUser":{"user_nick":"  图书馆用户  "}}}}
                """));
        var client = new TraceIntApiClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var nickname = await client.GetCurrentUserNicknameAsync("Authorization=a; SERVERID=b");

        Assert.Equal("图书馆用户", nickname);
    }

    [Fact]
    public async Task GetCurrentUserNicknameAsync_ReturnsNull_WhenNicknameIsBlank()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {"data":{"userAuth":{"currentUser":{"user_nick":"   "}}}}
                """));
        var client = new TraceIntApiClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var nickname = await client.GetCurrentUserNicknameAsync("Authorization=a; SERVERID=b");

        Assert.Null(nickname);
    }

    [Fact]
    public async Task GetLibrariesAsync_RetriesTransientHttpFailures_UsingSavedRetryCount()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("retry-1", null, HttpStatusCode.ServiceUnavailable)),
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("retry-2", null, HttpStatusCode.BadGateway)),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {"data":{"userAuth":{"reserve":{"libs":[{"lib_id":1,"lib_name":"自科阅览区一","lib_floor":"3","is_open":true}]}}}}
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default with { ApiTimeoutSeconds = 1, RetryCount = 2 }));

        var libraries = await client.GetLibrariesAsync("Authorization=a; SERVERID=b");

        Assert.Single(libraries);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal("自科阅览区一", libraries[0].Name);
    }

    [Fact]
    public async Task GetLibrariesAsync_RetriesTimedOutRequest_UsingSavedTimeoutSetting()
    {
        var handler = new SequenceHttpMessageHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return await SequenceHttpMessageHandler.JsonResponseAsync("{}");
            },
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {"data":{"userAuth":{"reserve":{"libs":[{"lib_id":2,"lib_name":"社科阅览区","lib_floor":"5","is_open":true}]}}}}
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default with { ApiTimeoutSeconds = 1, RetryCount = 1 }));

        var libraries = await client.GetLibrariesAsync("Authorization=a; SERVERID=b");

        Assert.Single(libraries);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, libraries[0].LibraryId);
    }

    [Fact]
    public async Task GetTraceIntServerTimeAsync_ReturnsServerDateHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var serverTime = new DateTimeOffset(2026, 9, 5, 20, 10, 0, TimeSpan.Zero);
        var handler = new SequenceHttpMessageHandler(
            (request, _) =>
            {
                capturedRequest = request;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Date = serverTime;
                return Task.FromResult(response);
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default with { RetryCount = 0 }));

        var actual = await client.GetTraceIntServerTimeAsync();

        Assert.Equal(serverTime, actual);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Head, capturedRequest.Method);
        Assert.Equal("wechat.v2.traceint.com", capturedRequest.Headers.Host);
    }

    [Fact]
    public async Task GetLibraryRuleAsync_ParsesRulePayload()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "libRule": {
                          "advance_booking": "1小时",
                          "lib_seat_ttl": "30",
                          "lib_hold_ttl": "30",
                          "lib_renew_time": "0",
                          "hold_reason": "{\"1\":{\"reason\":\"暂离保留\",\"time\":1800}}",
                          "close_start_date": null,
                          "close_end_date": null,
                          "open_time": 1774740600,
                          "open_time_str": "7:30",
                          "close_time": 1774792800,
                          "close_time_str": "22:00",
                          "lib_validate_time": -1
                        }
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var rule = await client.GetLibraryRuleAsync("Authorization=a; SERVERID=b", 117580);

        Assert.Equal(117580, rule.LibraryId);
        Assert.Equal("1小时", rule.AdvanceBooking);
        Assert.Equal("30", rule.SeatTtlMinutes);
        Assert.Equal("30", rule.HoldTtlMinutes);
        Assert.Equal("0", rule.RenewTimeMinutes);
        Assert.Equal("7:30", rule.OpenTimeText);
        Assert.Equal("22:00", rule.CloseTimeText);
        Assert.Equal(-1, rule.ValidateTime);
    }

    [Fact]
    public async Task GetLibraryLayoutAsync_KeepsTypeOneSeatsWithNonNumericNames_AndSkipsLayoutObjects()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "libs": [
                          {
                            "lib_id": 117580,
                            "lib_name": "自科阅览区一",
                            "lib_floor": "3",
                            "is_open": true,
                            "lib_layout": {
                              "seats_total": 4,
                              "seats_booking": 0,
                              "seats_used": 1,
                              "seats": [
                                { "x": 0, "y": 0, "key": "seat-12", "type": 1, "name": "12", "seat_status": 1, "status": false },
                                { "x": 1, "y": 0, "key": "seat-a12", "type": 1, "name": "A12", "seat_status": 1, "status": false },
                                { "x": 2, "y": 0, "key": "seat-room-1", "type": 1, "name": "研修间1", "seat_status": 1, "status": true },
                                { "x": 3, "y": 0, "key": "seat-fallback", "type": 1, "name": "", "seat_status": 1, "status": false },
                                { "x": 4, "y": 0, "key": "", "type": 1, "name": "无效座位", "seat_status": 1, "status": false },
                                { "x": 5, "y": 0, "key": "pillar", "type": 8, "name": "柱", "seat_status": 0, "status": false },
                                { "x": 6, "y": 0, "key": "west-label", "type": 8, "name": "西", "seat_status": 0, "status": false },
                                { "x": 7, "y": 0, "key": "desk", "type": 6, "name": null, "seat_status": 0, "status": false },
                                { "key": "layout-label", "name": "区域标签" }
                              ]
                            }
                          }
                        ]
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var layout = await client.GetLibraryLayoutAsync("Authorization=a; SERVERID=b", 117580);

        Assert.Equal(4, layout.Seats.Count);
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-12" && seat.SeatName == "12");
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-a12" && seat.SeatName == "A12");
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-room-1" && seat.SeatName == "研修间1" && seat.IsOccupied);
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-fallback" && seat.SeatName == "seat-fallback");
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatName == "无效座位");
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatName == "柱");
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatName == "西");
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatKey == "desk");
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatKey == "layout-label");
    }

    [Fact]
    public async Task GetPrereserveLibraryLayoutAsync_UsesPrereserveLibLayoutQuery_AndParsesSeatAvailability()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedPayload = null;
        var handler = new SequenceHttpMessageHandler(
            async (request, _) =>
            {
                capturedRequest = request;
                capturedPayload = await request.Content!.ReadAsStringAsync();
                return await SequenceHttpMessageHandler.JsonResponseAsync("""
                    {
                      "data": {
                        "userAuth": {
                          "prereserve": {
                            "libLayout": {
                              "seats_total": 3,
                              "seats_booking": 1,
                              "seats_used": 0,
                              "max_x": 5,
                              "max_y": 5,
                              "seats": [
                                { "x": 0, "y": 0, "key": "seat-free", "type": 1, "name": "225", "seat_status": 1, "status": false },
                                { "x": 1, "y": 0, "key": "seat-booked", "type": 1, "name": "226", "seat_status": 1, "status": true },
                                { "x": 2, "y": 0, "key": "desk", "type": 6, "name": null, "seat_status": 0, "status": false }
                              ]
                            }
                          }
                        }
                      }
                    }
                    """);
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var layout = await client.GetPrereserveLibraryLayoutAsync("Authorization=a; SERVERID=b", 117580);

        Assert.NotNull(capturedRequest);
        Assert.Contains("MicroMessenger", capturedRequest.Headers.UserAgent.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("App-Version", out var appVersions));
        Assert.Equal("2.0.14", Assert.Single(appVersions));
        Assert.Contains("prereserve", capturedPayload);
        Assert.Contains("libLayout(libId: $libId)", capturedPayload);
        Assert.Contains("\"libId\":117580", capturedPayload);
        Assert.Equal(117580, layout.LibraryId);
        Assert.Equal(2, layout.Seats.Count);
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-free" && seat.IsAvailable);
        Assert.Contains(layout.Seats, seat => seat.SeatKey == "seat-booked" && seat.IsOccupied);
        Assert.DoesNotContain(layout.Seats, seat => seat.SeatKey == "desk");
    }

    [Fact]
    public async Task SavePrereserveSeatAsync_UsesPrereserveSaveMutation_AndUpdatesServerIdCookie()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedPayload = null;
        var handler = new SequenceHttpMessageHandler(
            async (request, _) =>
            {
                capturedRequest = request;
                capturedPayload = await request.Content!.ReadAsStringAsync();
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"userAuth":{"prereserve":{"save":true}}}}""")
                };
                response.Headers.Add("Set-Cookie", "SERVERID=new-server|1775746288|1775746288; path=/");
                return response;
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var result = await client.SavePrereserveSeatAsync(
            "Authorization=a; SERVERID=old-server|1|1",
            371,
            "10,79");

        Assert.True(result.Submitted);
        Assert.Equal("Authorization=a; SERVERID=new-server|1775746288|1775746288", result.UpdatedCookie);
        Assert.NotNull(capturedRequest);
        Assert.Contains("MicroMessenger", capturedRequest.Headers.UserAgent.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("App-Version", out var appVersions));
        Assert.Equal("2.0.14", Assert.Single(appVersions));
        Assert.Contains("prereserve", capturedPayload);
        Assert.Contains("\"key\":\"10,79.\"", capturedPayload);
        Assert.Contains("\"libid\":371", capturedPayload);
    }

    [Fact]
    public async Task CancelPrereserveAsync_UsesOfficialPrereserveCancleMutation()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedPayload = null;
        var handler = new SequenceHttpMessageHandler(
            async (request, _) =>
            {
                capturedRequest = request;
                capturedPayload = await request.Content!.ReadAsStringAsync();
                return await SequenceHttpMessageHandler.JsonResponseAsync("""{"data":{"userAuth":{"prereserve":{"cancle":true}}}}""");
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var result = await client.CancelPrereserveAsync("Authorization=a; SERVERID=b");

        Assert.True(result);
        Assert.NotNull(capturedRequest);
        Assert.Contains("MicroMessenger", capturedRequest.Headers.UserAgent.ToString());
        Assert.True(capturedRequest.Headers.TryGetValues("App-Version", out var appVersions));
        Assert.Equal("2.0.14", Assert.Single(appVersions));
        Assert.Contains("mutation cancle", capturedPayload);
        Assert.Contains("\"operationName\":\"cancle\"", capturedPayload);
    }

    [Fact]
    public async Task GetReservationRecordsAsync_ReturnsTodayAndTomorrowReservations()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "reserve": {
                          "lib_id": 11,
                          "lib_name": "电子阅览室",
                          "seat_key": "3,4",
                          "seat_name": "304",
                          "date": "2026-05-25",
                          "exp_date": 1779746400
                        },
                        "getSToken": "today-token"
                      }
                    }
                  }
                }
                """),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "prereserve": {
                        "prereserve": {
                          "day": 1,
                          "lib_id": 22,
                          "lib_name": "社科阅览室",
                          "seat_key": "7,8",
                          "seat_name": "508",
                          "is_used": false,
                          "id": "tomorrow-token"
                        }
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var records = await client.GetReservationRecordsAsync("Authorization=a; SERVERID=b");

        Assert.Equal(2, records.Count);
        Assert.Contains(records, record =>
            record.Kind == ReservationRecordKind.Today &&
            record.LibraryName == "电子阅览室" &&
            record.SeatName == "304" &&
            record.ReservationToken == "today-token");
        Assert.Contains(records, record =>
            record.Kind == ReservationRecordKind.Tomorrow &&
            record.LibraryName == "社科阅览室" &&
            record.SeatName == "508" &&
            record.ReservationToken == "tomorrow-token");
    }

    [Fact]
    public async Task GetReservationRecordsAsync_ReturnsTomorrowReservation_WhenTodayReservationIsEmpty()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "reserve": null,
                        "getSToken": null
                      }
                    }
                  }
                }
                """),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "prereserve": {
                        "prereserve": {
                          "day": 1,
                          "lib_id": 22,
                          "lib_name": "社科阅览室",
                          "seat_key": "7,8",
                          "seat_name": "508",
                          "is_used": false,
                          "id": "tomorrow-token"
                        }
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var records = await client.GetReservationRecordsAsync("Authorization=a; SERVERID=b");

        var record = Assert.Single(records);
        Assert.Equal(ReservationRecordKind.Tomorrow, record.Kind);
        Assert.Equal("社科阅览室", record.LibraryName);
        Assert.Equal("508", record.SeatName);
        Assert.Equal("tomorrow-token", record.ReservationToken);
    }

    [Fact]
    public async Task GetReservationRecordsAsync_ParsesCompactNumericTomorrowReservationDay()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "reserve": null,
                        "getSToken": null
                      }
                    }
                  }
                }
                """),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "prereserve": {
                        "prereserve": {
                          "day": 20260526,
                          "lib_id": 22,
                          "lib_name": "社科阅览室",
                          "seat_key": "7,8",
                          "seat_name": "508",
                          "is_used": false,
                          "id": "tomorrow-token"
                        }
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var records = await client.GetReservationRecordsAsync("Authorization=a; SERVERID=b");

        var record = Assert.Single(records);
        Assert.Equal(new DateOnly(2026, 5, 26), record.ReservationDate);
    }

    [Fact]
    public async Task GetReservationRecordsAsync_MarksTodayReservationCheckedIn_WhenValidated()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "reserve": {
                          "lib_id": 11,
                          "lib_name": "电子阅览室",
                          "seat_key": "3,4",
                          "seat_name": "304",
                          "date": "2026-05-25",
                          "exp_date": 1779746400,
                          "validate_date": 1779742800,
                          "hold_date": "0"
                        },
                        "getSToken": "today-token"
                      }
                    }
                  }
                }
                """),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "prereserve": {
                        "prereserve": null
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var records = await client.GetReservationRecordsAsync("Authorization=a; SERVERID=b");

        var record = Assert.Single(records);
        Assert.True(record.IsCheckedIn);
        Assert.True(record.CanCancel);
    }

    [Fact]
    public async Task GetReservationRecordsAsync_DoesNotTreatZeroHoldDateAsCheckedIn()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "reserve": {
                        "reserve": {
                          "lib_id": 11,
                          "lib_name": "电子阅览室",
                          "seat_key": "3,4",
                          "seat_name": "304",
                          "date": "2026-05-25",
                          "exp_date": 1779746400,
                          "validate_date": 0,
                          "hold_date": "0"
                        },
                        "getSToken": "today-token"
                      }
                    }
                  }
                }
                """),
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "data": {
                    "userAuth": {
                      "prereserve": {
                        "prereserve": null
                      }
                    }
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var records = await client.GetReservationRecordsAsync("Authorization=a; SERVERID=b");

        var record = Assert.Single(records);
        Assert.False(record.IsCheckedIn);
        Assert.True(record.CanCancel);
    }

    [Fact]
    public async Task GetLibrariesAsync_PersistsNewAuthorizationCookie_FromResponseSetCookie()
    {
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials(
                "Authorization=old-token; SERVERID=old-server",
                SessionSource.ManualCookie,
                DateTimeOffset.Now.AddMinutes(-5),
                true)
        };
        var credentialStore = new FakeCredentialStore();
        var handler = new SequenceHttpMessageHandler(
            (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":{"userAuth":{"reserve":{"libs":[{"lib_id":1,"lib_name":"自科阅览区一","lib_floor":"3","is_open":true}]}}}}
                        """)
                };
                response.Headers.Add("Set-Cookie", "Authorization=new-token; path=/");
                response.Headers.Add("Set-Cookie", "SERVERID=new-server; path=/");
                return Task.FromResult(response);
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default),
            runtimeState,
            credentialStore,
            new ActivityLogService());

        await client.GetLibrariesAsync("Authorization=old-token; SERVERID=old-server");

        Assert.Equal("Authorization=new-token; SERVERID=new-server", runtimeState.Session.Cookie);
        Assert.Equal(1, credentialStore.SaveCalls);
        Assert.Equal(runtimeState.Session, credentialStore.StoredSession);
    }

    [Fact]
    public async Task GetLibrariesAsync_UpdatesRuntimeOnly_WhenResponseOnlyUpdatesServerId()
    {
        var runtimeState = new AppRuntimeState
        {
            Session = new SessionCredentials(
                "Authorization=old-token; SERVERID=old-server",
                SessionSource.ManualCookie,
                DateTimeOffset.Now.AddMinutes(-5),
                true)
        };
        var credentialStore = new FakeCredentialStore();
        var handler = new SequenceHttpMessageHandler(
            (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"data":{"userAuth":{"reserve":{"libs":[{"lib_id":1,"lib_name":"自科阅览区一","lib_floor":"3","is_open":true}]}}}}
                        """)
                };
                response.Headers.Add("Set-Cookie", "SERVERID=new-server; path=/");
                return Task.FromResult(response);
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default),
            runtimeState,
            credentialStore,
            new ActivityLogService());

        await client.GetLibrariesAsync("Authorization=old-token; SERVERID=old-server");

        Assert.Equal("Authorization=old-token; SERVERID=new-server", runtimeState.Session.Cookie);
        Assert.Equal(0, credentialStore.SaveCalls);
        Assert.Null(credentialStore.StoredSession);
    }

    [Fact]
    public async Task RefreshPrereservePageAsync_SendsBothAntiBotQueries()
    {
        var payloads = new List<string>();
        var handler = new SequenceHttpMessageHandler(
            async (request, _) =>
            {
                payloads.Add(await request.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"userAuth":{"prereserve":{"prereserve":null}}}}""")
                };
            },
            async (request, _) =>
            {
                payloads.Add(await request.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"userAuth":{"prereserve":{"libs":[]},"oftenseat":{"prereserveList":[]}}}}""")
                };
            });

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        await client.RefreshPrereservePageAsync("Authorization=a; SERVERID=b");

        Assert.Equal(2, payloads.Count);
        Assert.Contains("query prereserve", payloads[0]);
        Assert.Contains("prereserveAuto", payloads[1]);
    }

    [Fact]
    public async Task GetLibrariesAsync_ThrowsStructuredTraceIntApiException_ForExpiredCookieResponse()
    {
        var handler = new SequenceHttpMessageHandler(
            (_, _) => SequenceHttpMessageHandler.JsonResponseAsync("""
                {
                  "errors": [
                    {
                      "msg": "access denied!",
                      "code": 40001
                    }
                  ],
                  "data": {
                    "userAuth": null
                  }
                }
                """));

        var client = new TraceIntApiClient(
            new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            },
            new FakeProtocolTemplateStore(new ProtocolTemplateSet(
                "https://example.com/ReplaceMeByCode",
                "{\"query\":\"libraries\"}",
                "{\"query\":\"layout\"}",
                "{\"query\":\"rule\"}",
                "{\"query\":\"reservation\"}",
                "{\"query\":\"reserve\"}",
                "{\"query\":\"cancel\"}")),
            new FakeSettingsService(AppSettings.Default));

        var exception = await Assert.ThrowsAsync<TraceIntApiException>(() => client.GetLibrariesAsync("Authorization=a; SERVERID=b"));

        Assert.Equal(40001, exception.ErrorCode);
        Assert.Equal("access denied!", exception.RemoteMessage);
        Assert.True(exception.IsAuthorizationDenied);
    }

    [Fact]
    public void BuildCookieHeaderFromResponseCookies_MatchesWinformOrdering()
    {
        var cookies = TraceIntApiClient.BuildCookieHeaderFromResponseCookies(
        [
            "SERVERID=test-server|1775746288|1775746288",
            "Authorization=eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9"
        ]);

        Assert.Equal(
            "Authorization=eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9; SERVERID=test-server|1775746288|1775746288",
            cookies);
    }

    [Fact]
    public void BuildCookieHeaderFromResponseCookies_Throws_WhenCookieCollectionIsNull()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TraceIntApiClient.BuildCookieHeaderFromResponseCookies(null));

        Assert.Equal("响应报文返回的Cookie为空", exception.Message);
    }

    [Fact]
    public void BuildCookieHeaderFromResponseCookies_Throws_WhenCookieCollectionHasFewerThanTwoItems()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TraceIntApiClient.BuildCookieHeaderFromResponseCookies(
        [
            "Authorization=eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9"
        ]));

        Assert.Equal("Cookie不包含关键身份信息，可能是code过期，重新填写含code的链接", exception.Message);
    }

    [Fact]
    public void ThrowIfCookieResponseFailed_ThrowsHttpRequestException_WhenStatusFailedWithoutCookies()
    {
        var response = new RestResponse
        {
            StatusCode = HttpStatusCode.Forbidden,
            StatusDescription = "Forbidden",
            ResponseStatus = ResponseStatus.Completed
        };

        var exception = Assert.Throws<HttpRequestException>(() => TraceIntApiClient.ThrowIfCookieResponseFailed(response, []));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("HTTP 403 Forbidden", exception.Message);
    }

    [Fact]
    public void ThrowIfCookieResponseFailed_AllowsCookieExtraction_WhenFailedResponseAlreadyContainsCookies()
    {
        var response = new RestResponse
        {
            StatusCode = HttpStatusCode.Found,
            StatusDescription = "Found",
            ResponseStatus = ResponseStatus.Completed
        };

        TraceIntApiClient.ThrowIfCookieResponseFailed(response,
        [
            "SERVERID=b",
            "Authorization=a"
        ]);
    }
}
