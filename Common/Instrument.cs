using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MarketData.Common
{
    public record Specifications(int Depth);
    public record Instrument(int Id, string Symbol, Specifications Specifications);
}

