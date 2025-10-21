using System;

namespace Demo.Models
{
    public class Revolution
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Country { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
    }
}
