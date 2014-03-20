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
        .Where(a => a.ApplicationName.StartsWith("Ch"))
        .Skip(2)
        .Take(5)
        .ToListAsync();            
}
```
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