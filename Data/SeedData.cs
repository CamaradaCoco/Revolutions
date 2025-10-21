using System;
using System.Linq;
using Demo.Models;

namespace Demo.Data
{
    public static class SeedData
    {
        public static void EnsureSeedData(RevolutionContext db)
        {
            if (db.Revolutions.Any()) return;

            db.Revolutions.AddRange(
                new Revolution
                {
                    Name = "Example Revolution",
                    StartDate = new DateTime(1900, 1, 1),
                    EndDate = new DateTime(1900, 12, 31),
                    Country = "Exampleland",
                    Latitude = null,
                    Longitude = null,
                    Type = "Political",
                    Description = "Seed item for initial DB"
                }
            );

            db.SaveChanges();
        }
    }
}