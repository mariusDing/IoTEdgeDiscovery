using System;
using System.Collections.Generic;
using System.Text;

namespace IotEdgeModule2
{
    public class SensorModel
    {
        public Machine machine { get; set; }

        public Ambient ambient { get; set; }

        public DateTime timeCreated { get; set; }
    }

    public class Machine
    {
        public double temperature { get; set; }

        public double pressure { get; set; }
    }

    public class Ambient
    {
        public double temperature { get; set; }

        public double humidity { get; set; }
    }
}
