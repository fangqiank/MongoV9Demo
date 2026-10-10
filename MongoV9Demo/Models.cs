using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoV9Demo
{
    public class Passenger
    {
        [BsonId]
        public ObjectId Id { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string PassportNumber { get; set; } = "";
    }

    public class Booking
    {
        [BsonId]
        public ObjectId Id { get; set; }
        public ObjectId PassengerId { get; set; }
        public decimal Fare { get; set; }
        public decimal Tax { get; set; }
    }
}
