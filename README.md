## MongoDB C# Driver - Async support

UNSTABLE CODE - USE AT YOUR OWN RISK

Async version of the MongoDB official driver. Async LINQ support (ToListAsync, FirstOrDefaultAsync, etc.) based on EF6.1 implementation.

Sample usage:
```csharp

private async Task RunAsync()
{
    var client = new MongoClient(connectionString);
    var server = client.GetServer();
    var database = server.GetDatabase("MongoTest");

    var collection = database.GetCollection<ApplicationConfiguration>("ApplicationConfigurations");

    var cursor = collection.FindAll();
    var count = await cursor.CountAsync();

    var enumerator = await cursor.GetEnumeratorAsync();
    while (await enumerator.MoveNextAsync())
    {
        var item = enumerator.Current;
    }

    var queryable = await collection
        .AsQueryable()                
        .Where(a => a.ApplicationName.StartsWith("Test"))
        .Skip(2)
        .Take(5)
        .ToListAsync();

    var applicationConfiguration = new ApplicationConfiguration()
    {
        ApplicationConfigurationId = Guid.NewGuid(),
        ApplicationName = "Test application",
        SeverityLevel = System.Diagnostics.TraceEventType.Verbose,
        SeverityFilterCollection = new List<SeverityFilter>()
        {
            new SeverityFilter()
            {
                SeverityFilterId = Guid.NewGuid(),
                CategoryName = "Category",
                MachineName = "SERVER1",
                SeverityLevel = System.Diagnostics.TraceEventType.Error,
                MessageTitle = "Title 1"
            },
            new SeverityFilter()
            {
                SeverityFilterId = Guid.NewGuid(),
                CategoryName = "Category",
                MachineName = "SERVER2",
                SeverityLevel = System.Diagnostics.TraceEventType.Error,
                MessageTitle = "Title 2"
            }
        }
    };

    var insertResult = await collection.InsertAsync(applicationConfiguration);
    var updatedApplicationCofiguration = await collection.FindOneByIdAsAsync<ApplicationConfiguration>(applicationConfiguration.ApplicationConfigurationId);
    updatedApplicationCofiguration.ApplicationName = "Test application Updated";
    var updateResult = await collection.SaveAsync(updatedApplicationCofiguration);
}
```

Profiling:

```json
/* 0 */
{
  "op" : "command",
  "ns" : "MongoTest.$cmd",
  "command" : {
    "count" : "ApplicationConfigurations"
  },
  "ntoreturn" : 1,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(21),
      "w" : NumberLong(0)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(4),
      "w" : NumberLong(2)
    }
  },
  "responseLength" : 48,
  "millis" : 0,
  "ts" : ISODate("2014-03-20T16:43:25.889Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}

/* 1 */
{
  "op" : "query",
  "ns" : "MongoTest.ApplicationConfigurations",
  "query" : { },
  "ntoreturn" : 0,
  "ntoskip" : 0,
  "nscanned" : 26,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(28979),
      "w" : NumberLong(0)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(8),
      "w" : NumberLong(3)
    }
  },
  "nreturned" : 26,
  "responseLength" : 327738,
  "millis" : 29,
  "ts" : ISODate("2014-03-20T16:43:25.935Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}

/* 2 */
{
  "op" : "query",
  "ns" : "MongoTest.ApplicationConfigurations",
  "query" : {
    "ApplicationName" : /^Test/s
  },
  "ntoreturn" : 5,
  "ntoskip" : 2,
  "nscanned" : 26,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(167),
      "w" : NumberLong(0)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(4),
      "w" : NumberLong(10)
    }
  },
  "nreturned" : 1,
  "responseLength" : 530,
  "millis" : 0,
  "ts" : ISODate("2014-03-20T16:43:26.043Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}

/* 3 */
{
  "op" : "insert",
  "ns" : "MongoTest.ApplicationConfigurations",
  "ninserted" : 1,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(0),
      "w" : NumberLong(158)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(0),
      "w" : NumberLong(7)
    }
  },
  "millis" : 0,
  "ts" : ISODate("2014-03-20T16:43:26.08Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}

/* 4 */
{
  "op" : "query",
  "ns" : "MongoTest.ApplicationConfigurations",
  "query" : {
    "_id" : new BinData(3, "yo+dw6UsHk+5/K9a+MKysQ==")
  },
  "ntoreturn" : 1,
  "ntoskip" : 0,
  "nscanned" : 1,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(174),
      "w" : NumberLong(0)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(3),
      "w" : NumberLong(3)
    }
  },
  "nreturned" : 1,
  "responseLength" : 514,
  "millis" : 0,
  "ts" : ISODate("2014-03-20T16:43:26.098Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}

/* 5 */
{
  "op" : "update",
  "ns" : "MongoTest.ApplicationConfigurations",
  "query" : {
    "_id" : new BinData(3, "yo+dw6UsHk+5/K9a+MKysQ==")
  },
  "updateobj" : {
    "_id" : new BinData(3, "yo+dw6UsHk+5/K9a+MKysQ=="),
    "ApplicationName" : "Test application Updated",
    "LogRepositoryName" : "Test application UpdatedLogs",
    "SeverityLevel" : 16,
    "SeverityLevelFlat" : 16,
    "SeverityFilterCollection" : [{
        "_id" : new BinData(3, "ZBf34q6j+UCNug1a0793Kg=="),
        "CategoryName" : "Category",
        "MachineName" : "SERVER1",
        "MessageTitle" : "Title 1",
        "SeverityLevel" : 2,
        "SeverityLevelFlat" : 2
      }, {
        "_id" : new BinData(3, "H56TdPCeS0amsPkNmbDsOw=="),
        "CategoryName" : "Category",
        "MachineName" : "SERVER2",
        "MessageTitle" : "Title 2",
        "SeverityLevel" : 2,
        "SeverityLevelFlat" : 2
      }]
  },
  "nscanned" : 1,
  "moved" : true,
  "nmoved" : 1,
  "nupdated" : 1,
  "keyUpdates" : 0,
  "numYield" : 0,
  "lockStats" : {
    "timeLockedMicros" : {
      "r" : NumberLong(0),
      "w" : NumberLong(187)
    },
    "timeAcquiringMicros" : {
      "r" : NumberLong(0),
      "w" : NumberLong(6)
    }
  },
  "millis" : 0,
  "ts" : ISODate("2014-03-20T16:43:26.118Z"),
  "client" : "127.0.0.1",
  "allUsers" : [],
  "user" : ""
}
```

Nuget: mongodb.async

---------------------------------------------

documentation: http://www.mongodb.org/display/DOCS/CSharp+Language+Center
apidoc: http://api.mongodb.org/csharp/

source code: http://github.com/mongodb/mongo-csharp-driver

mongodb home: http://www.mongodb.org/

### Questions and Bug Reports

 * mailing list: http://groups.google.com/group/mongodb-user
 * jira: http://jira.mongodb.org/

### Change Log

http://jira.mongodb.org/browse/CSHARP

### Maintainers:
* Robert Stam               robert@mongodb.com
* Craig Wilson              craig.wilson@mongodb.com

### Contributors:
* Bit Diffusion Limited     code@bitdiff.com
* Alex Brown                https://github.com/alexjamesbrown
* Justin Dearing            zippy1981@gmail.com
* Dan DeBilt                dan.debilt@gmail.com
* Teun Duynstee             teun@duynstee.com
* Einar Egilsson            https://github.com/einaregilsson
* Ken Egozi                 mail@kenegozi.com
* Daniel Goldman            daniel@stackwave.com
* Simon Green               simon@captaincodeman.com
* Nik Kolev                 nkolev@gmail.com
* Oleg Kosmakov             kosmakoff@gmail.com
* Brian Knight              brianknight10@gmail.com  
* Richard Kreuter           richard@10gen.com
* Kevin Lewis               kevin.l.lewis@gmail.com
* Dow Liu                   redforks@gmail.com
* Alex Lyman                mail.alex.lyman@gmail.com
* Alexander Nagy            optimiz3@gmail.com
* Sridhar Nanjundeswaran    https://github.com/sridharn
* Andrew Rondeau            github@andrewrondeau.com
* Ed Rooth                  edward.rooth@wallstreetjapan.com
* Pete Smith                roysvork@gmail.com
* staywellandy              https://github.com/staywellandy
* Testo                     test1@doramail.com   

If you have contributed and we have neglected to add you to this list please contact one of the maintainers to be added to the list (with apologies).
