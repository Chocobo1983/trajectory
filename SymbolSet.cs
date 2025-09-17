using System.ComponentModel.DataAnnotations;

namespace Infozahyst.RSAAS.Common.Tools.NatoApp6d;

public enum SymbolSet
{
    [Display(Name = "Air Units")]
    AirUnits = 1,

    [Display(Name = "Land Units")]
    LandUnits = 10,

    [Display(Name = "Land Equipment")]
    LandEquipment = 15,

    [Display(Name = "Signals Intelligence - Land")]
    SignalsIntelligenceLand = 52,
}
