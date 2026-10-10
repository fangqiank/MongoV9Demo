using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoV9Demo;

const string ConnStr = "mongodb://192.168.1.180:27017/?directConnection=true";
var client = new MongoClient(ConnStr);
var db = client.GetDatabase("travel");
Console.WriteLine($"MongoDB 版本: {(await db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1)))["version"]}");

var passengers = db.GetCollection<Passenger>("passengers");
var bookings = db.GetCollection<Booking>("bookings");

await SeedAsync(passengers, bookings);
Console.WriteLine($"Passengers: {await passengers.CountDocumentsAsync(_ => true)}");
Console.WriteLine($"Bookings:   {await bookings.CountDocumentsAsync(_ => true)}");

//Join 聚合（V9 自动优化）
await DemoJoinAsync(bookings);

//query 静态类方式构建管道（JSON Deserialize）
await DemoQueryClassAsync(bookings);

//原生哈希（8.3+ 操作符，非 9.0 新增）
await DemoHashAsync(passengers);

//filter 创建方式
await DemoFiltersAsync(passengers, bookings);

//BsonSerializer.Deserialize
await DemoDeserializeAsync(passengers);

static async Task SeedAsync(
    IMongoCollection<Passenger> passengers,
    IMongoCollection<Booking> bookings
    )
{
    var rnd = new Random(42);

    // 两个集合各自判断，部分 seed 失败后重跑能补齐缺口
    if (await passengers.CountDocumentsAsync(_ => true) == 0)
    {
        var pax = Enumerable.Range(0, 10_000).Select(i => new Passenger
        {
            Id = ObjectId.GenerateNewId(),
            FirstName = $"First{i}",
            LastName = i % 100 == 0 ? "Jovanovic" : $"Last{i}",
            PassportNumber = $"P{i:D8}{rnd.Next(1000, 9999)}"
        }).ToList();

        await passengers.InsertManyAsync(pax);
    }

    if (await bookings.CountDocumentsAsync(_ => true) == 0)
    {
        var ids = await passengers.Find(_ => true).Project(p => p.Id).ToListAsync();
        var books = Enumerable.Range(0, 100_000).Select(i => new Booking
        {
            Id = ObjectId.GenerateNewId(),
            PassengerId = ids[rnd.Next(ids.Count)],
            Fare = rnd.Next(20, 100),
            Tax = rnd.Next(1, 20)
        }).ToList();

        await bookings.InsertManyAsync(books);
    }
}

static async Task DemoJoinAsync(IMongoCollection<Booking> bookings)
{
    Console.WriteLine("\n=== Join 聚合 (V9 自动 filter 上移) ===");

    // 管道语法 8.x 与 9.0 完全相同，无需改写；差异只在服务端：
    // V9 自动把过滤条件上推进 $lookup，8.x 会全量 join 完才过滤（数据量大时明显更慢）
    var pipeline = new BsonDocument[]
    {
        new("$lookup", new BsonDocument
        {
            { "from", "passengers" },
            { "localField", "PassengerId" },
            { "foreignField", "_id" },
            { "as", "passenger" }
        }),
        new("$unwind", "$passenger"),
        // $addFields 与 $set 同义（Query 类里写的是 $set），统一叫法
        new("$addFields", new BsonDocument("total",
            new BsonDocument("$add", new BsonArray { "$Fare", "$Tax" }))),
        new("$match", new BsonDocument("total",
            new BsonDocument("$gt", 60))),
        new("$project", new BsonDocument
        {
            { "_id", 1 }, { "total", 1 }, { "passenger", 1 }
        }),
        new("$limit", 5)
    };

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var results = await bookings.Aggregate<BsonDocument>(pipeline).ToListAsync();
    sw.Stop();

    Console.WriteLine($"返回 {results.Count} 条，耗时 {sw.ElapsedMilliseconds} ms");
}

static async Task DemoHashAsync(IMongoCollection<Passenger> passengers)
{
    Console.WriteLine("\n=== 哈希: 8.3+ $hash vs 8.x 替代方案 ===");

    // A. $hash（8.3+，SHA-256，加密强度）
    var pipeline = new BsonDocument[]
    {
        new("$addFields", new BsonDocument("passportHash",
            new BsonDocument("$hash", new BsonDocument
            {
                { "input", "$PassportNumber" },
                { "algorithm", "sha256" }   // 8.3+ 原生支持
            }))),
        new("$project", new BsonDocument
        {
            { "PassportNumber", 1 },
            { "passportHash", 1 }
        }),
        new("$limit", 3)
    };

    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await passengers.Aggregate<BsonDocument>(pipeline).ToListAsync();
        sw.Stop();

        foreach (var doc in results)
        {
            Console.WriteLine($"  A $hash:            {doc["PassportNumber"]} -> {doc["passportHash"]}");
        }
        Console.WriteLine($"  耗时 {sw.ElapsedMilliseconds} ms");
    }
    catch (MongoCommandException ex)
    {
        Console.WriteLine($"  ⚠ $hash 需要 MongoDB 8.3+（当前服务器不支持）: {ex.Message}");
    }

    // B. $toHashedIndexKey（5.0+，8.x 原生替代）：哈希索引同款算法，快，但 MD5、非加密强度
    var pipelineB = new BsonDocument[]
    {
        new("$addFields", new BsonDocument("passportHash",
            new BsonDocument("$toHashedIndexKey", "$PassportNumber"))),
        new("$project", new BsonDocument
        {
            { "PassportNumber", 1 },
            { "passportHash", 1 }
        }),
        new("$limit", 3)
    };

    Console.WriteLine("  --- 8.x 替代: $toHashedIndexKey (5.0+, MD5, 非加密强度) ---");
    var resultsB = await passengers.Aggregate<BsonDocument>(pipelineB).ToListAsync();
    foreach (var doc in resultsB)
    {
        Console.WriteLine($"  B $toHashedIndexKey: {doc["PassportNumber"]} -> {doc["passportHash"]}");
    }

    // C. $function JS（8.x 通用逃生舱）。注意：服务端 JS 沙箱无 crypto 库，
    //    SHA-256 在 8.x 服务端做不到（除非内嵌纯 JS 实现）——只能演示简单转换
    var pipelineC = new BsonDocument[]
    {
        new("$addFields", new BsonDocument("passportLower",
            new BsonDocument("$function", new BsonDocument
            {
                { "body", "function(s) { return s.toLowerCase(); }" },
                { "args", new BsonArray { "$PassportNumber" } },
                { "lang", "js" }
            }))),
        new("$project", new BsonDocument
        {
            { "PassportNumber", 1 },
            { "passportLower", 1 }
        }),
        new("$limit", 2)
    };

    Console.WriteLine("  --- 8.x 替代: $function (JS 逃生舱，SHA-256 服务端不可行) ---");
    try
    {
        var resultsC = await passengers.Aggregate<BsonDocument>(pipelineC).ToListAsync();
        foreach (var doc in resultsC)
        {
            Console.WriteLine($"  C $function:        {doc["PassportNumber"]} -> {doc["passportLower"]}");
        }
    }
    catch (MongoCommandException ex)
    {
        Console.WriteLine($"  ⚠ $function 需服务器开启 JS 脚本: {ex.Message.Split('\n')[0]}");
    }
}

static async Task DemoFiltersAsync(
    IMongoCollection<Passenger> passengers,
    IMongoCollection<Booking> bookings
    )
{
    Console.WriteLine("\n=== Filter 创建方式 ===");

    // ① Builders 组合（& | ! 重载），可复用、可传参
    var b = Builders<Passenger>.Filter;
    var f1 = b.Eq(p => p.LastName, "Last42")
           & b.Regex(p => p.PassportNumber, new BsonRegularExpression("^P00000042"));
    Console.WriteLine($"① Builders+&:  {(await passengers.Find(f1).ToListAsync()).Count} 条 (LastName=Last42 且护照号 ^P00000042)");

    // ② lambda 直写（LINQ3 翻译），静态字段首选
    var n2 = await bookings.CountDocumentsAsync(x => x.Fare > 50 && x.Tax < 10);
    Console.WriteLine($"② lambda:      {n2} 条 (Fare>50 && Tax<10)");

    // ③ JSON 字符串隐式转换，从 shell 查询翻译过来最省事
    FilterDefinition<Passenger> f3 = "{ LastName: 'Jovanovic' }";
    Console.WriteLine($"③ JSON 字符串: {(await passengers.Find(f3).ToListAsync()).Count} 条 (shell 语法)");

    // ④ BsonDocument，字段名/值运行期才知道时用
    var f4 = new BsonDocument("Tax", new BsonDocument("$gte", 15));
    Console.WriteLine($"④ BsonDocument:{(await bookings.CountDocumentsAsync(f4))} 条 (Tax>=15，动态构建)");

    // ⑤ 常用操作符：In
    var f5 = Builders<Booking>.Filter.In(x => x.Fare, new[] { 20m, 30m, 40m });
    Console.WriteLine($"⑤ In:          {(await bookings.CountDocumentsAsync(f5))} 条 (Fare ∈ 20/30/40)");

    // ⑥ V9 QE 前缀/后缀 filter：驱动 3.12 API 真实存在，
    //    实际执行需 9.0 服务器 + KMS + encryptedFields，此处仅构建打印。
    //    8.x 无前缀/后缀 QE 查询；等价做法：应用侧预存确定性加密 token 字段，用相等查询匹配
    var fqe = Builders<Passenger>.Filter.And(
        Builders<Passenger>.Filter.EncStrStartsWith(p => p.LastName, "Jovan"),
        Builders<Passenger>.Filter.EncStrEndsWith(p => p.PassportNumber, "4821"));
    // ToJson 只能输出 _t 判别符——QE filter 的 $encStr* wire 形式需加密上下文才渲染
    Console.WriteLine("⑥ QE filter:   已构建（$expr.$and + $encStrStartsWith{input,prefix} + $encStrEndsWith{input,suffix}）");
    Console.WriteLine("                （执行需 9.0 + Queryable Encryption）");
}

static async Task DemoDeserializeAsync(IMongoCollection<Passenger> passengers)
{
    Console.WriteLine("\n=== BsonSerializer.Deserialize ===");

    // ① BsonDocument → POCO：真实场景是 Aggregate<BsonDocument> 的结果，
    //    这里用 POCO→Bson→POCO 往返演示同一个转换
    var doc = (await passengers.Find(p => p.LastName == "Last42").FirstAsync()).ToBsonDocument();
    var pax = BsonSerializer.Deserialize<Passenger>(doc);
    Console.WriteLine($"① BsonDocument→POCO: {pax.FirstName} {pax.LastName}, 护照 {pax.PassportNumber}");

    // ② JSON 字符串 → POCO：_id 走 [BsonId]，字段名按属性名原样匹配
    var pax2 = BsonSerializer.Deserialize<Passenger>(
        "{ _id: { '$oid': '000000000000000000000000' }, FirstName: 'A', LastName: 'B', PassportNumber: 'X' }");
    Console.WriteLine($"② JSON→POCO:        LastName={pax2.LastName}, Id={pax2.Id}");

    // ③ 经典坑：文档含未映射字段 → 默认抛 FormatException
    var extra = new BsonDocument { { "FirstName", "A" }, { "LastName", "B" }, { "PassportNumber", "X" }, { "Extra", 1 } };
    try { BsonSerializer.Deserialize<Passenger>(extra); }
    catch (FormatException ex)
    {
        Console.WriteLine($"③ 未映射字段默认报错: {ex.Message.Split('\n')[0]}");
        Console.WriteLine("   解法: 类上加 [BsonIgnoreExtraElements]，或 ConventionPack 全局设置");
    }
}

static async Task DemoQueryClassAsync(IMongoCollection<Booking> bookings)
{
    Console.WriteLine("\n=== Query.Create()（JSON Deserialize 构建管道）===");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var results = await bookings.Aggregate<BsonDocument>(Query.Create()).ToListAsync();
    sw.Stop();
    Console.WriteLine($"返回 {results.Count} 条，耗时 {sw.ElapsedMilliseconds} ms（total<60，无 limit）");
}

public static class Query
{
    public static BsonDocument[] Create() => BsonSerializer.Deserialize<BsonDocument[]>("""
        [
            { $lookup: {
                from: 'passengers',
                localField: 'PassengerId', foreignField: '_id', as: 'passenger'
            } },
            { $unwind: '$passenger' },
            { $set: { total: { $add: [ '$Fare', '$Tax' ] } } },
            { $match: { total: { $lt: 60 } } },
            { $project: { _id: 1, total: 1, passenger: 1 } }
        ]
        """);
}
