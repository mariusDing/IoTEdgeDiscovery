using System;
using System.Collections.Generic;

namespace IotEdgeModule1
{
    public class VisionResponse
    {
        public DateTime? Created { get; set; }

        public string Id { get; set; }

        public string Iteration { get; set; }

        public List<VisionPrediction> Predictions { get; set; } = new List<VisionPrediction>();

        public class VisionPrediction
        {
            public object BoundingBox { get; set; }
            public double Probability { get; set; }
            public string TagId { get; set; }
            public string TagName { get; set; }
        }
    }
}
