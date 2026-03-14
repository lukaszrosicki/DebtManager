using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DebtManager.Models
{
    public enum CzestotliwoscRaty
    {
        Miesieczna = 1,
        Kwartalna = 3,
        Roczna = 12,
        Inna = 0
    }

    public enum TypRat
    {
        Stale = 0,    // Anuitetowe
        Malejace = 1  // Liniowe
    }

    public enum TypNadplaty
    {
        Kapital = 0,
        Proporcjonalnie = 1,
        Reczna = 2
    }

    public enum EfektNadplaty
    {
        ObnizenieRat = 0,
        SkrocenieOkresu = 1
    }

    // --- ENCJE BAZODANOWE ---

    public class Dlug
    {
        public int Id { get; set; }
        [Required]
        public string Nazwa { get; set; } = "Kredyt Hipoteczny";
        
        public decimal WartoscPoczatkowa { get; set; }
        
        [DataType(DataType.Date)]
        public DateTime DataPierwszejRaty { get; set; } = DateTime.Today;
        public int DzienRaty { get; set; } // Dzień miesiąca
        
        public CzestotliwoscRaty Czestotliwosc { get; set; } = CzestotliwoscRaty.Miesieczna;
        public int InnaCzestotliwoscDni { get; set; } // Jeśli wybrano "Inna"
        
        public decimal Oprocentowanie { get; set; } // w %
        public TypRat TypRat { get; set; }
        public bool WyrownanieWPierwszejRacie { get; set; } = false; // false = w ostatniej

        [StringLength(3)]
        public string Currency { get; set; } = "PLN";

        [Column(TypeName = "decimal(18, 4)")]
        public decimal ExchangeRate { get; set; } = 1m;

        public int LiczbaRat { get; set; } = 12; // Okres kredytowania

        public virtual ICollection<Rata> Raty { get; set; } = new List<Rata>();
        public virtual ICollection<Nadplata> Nadplaty { get; set; } = new List<Nadplata>();
        public virtual ICollection<ZmianaOprocentowania> ZmianyOprocentowania { get; set; } = new List<ZmianaOprocentowania>();
    }

    public class Rata
    {
        public int Id { get; set; }
        public int DlugId { get; set; }
        public virtual Dlug Dlug { get; set; } = default!;

        public int NumerRaty { get; set; }
        public DateTime DataRaty { get; set; }
        
        // Wartości wyliczone lub wpisane ręcznie
        public decimal Kapital { get; set; }
        public decimal Odsetki { get; set; }
        public decimal Calkowita => Kapital + Odsetki;
        public decimal PozostaloKapitalu { get; set; }
        public decimal OprocentowanieRaty { get; set; } // Oprocentowanie w momencie tej raty

        public bool CzyEdytowanaRecznie { get; set; } = false; // Jeśli true, algorytm nie nadpisuje tej raty przy przeliczaniu
        public bool CzyOplacona { get; set; } = false;
    }

    public class Nadplata
    {
        public int Id { get; set; }
        public int DlugId { get; set; }
        public DateTime Data { get; set; }
        public decimal Kwota { get; set; }
        public TypNadplaty Typ { get; set; }
        public EfektNadplaty Efekt { get; set; }
        
        // Dla typu proporcjonalnego (ile % poszło na odsetki)
        public decimal CzescOdsetkowa { get; set; } 

        public bool CzyEdytowanaRecznie { get; set; } = false;
    }

    public class ZmianaOprocentowania
    {
        public int Id { get; set; }
        public int DlugId { get; set; }
        public DateTime DataZmiany { get; set; }
        public decimal NoweOprocentowanie { get; set; }
    }

    // --- VIEW MODELS ---

    // Element osi czasu (może być ratą, nadpłatą lub zmianą %)
    public class ElementOsiCzasu
    {
        public DateTime Data { get; set; }
        public string TypWiersza { get; set; } = "Rata"; // "Rata", "Nadplata", "ZmianaOprocentowania"
        
        // Pola dla Raty
        public Rata? Rata { get; set; }
        
        // Pola dla Nadpłaty
        public Nadplata? Nadplata { get; set; }
        
        // Pola dla Zmiany %
        public ZmianaOprocentowania? ZmianaOprocentowania { get; set; }
    }

    public class DebtDashboardViewModel
    {
        public List<Dlug> ListaDlugow { get; set; } = new List<Dlug>();
        public Dlug? WybranyDlug { get; set; }
        public List<ElementOsiCzasu> OsCzasu { get; set; } = new List<ElementOsiCzasu>();

        // Formularze
        public Dlug KonfiguracjaDlugu { get; set; } = new Dlug();
        
        // Stan edycji
        public int? EdytowanaRataId { get; set; }
        public int? EdytowanaNadplataId { get; set; }
        public int? EdytowanaZmianaId { get; set; }
        public bool PrzeliczReszte { get; set; } = true;
        
        // UI Helper
        public bool CzySymulacja { get; set; } = false;
        // Kalkulator
        public decimal CalcNadplataMiesieczna { get; set; }
        public bool CalcStalaRataLaczna { get; set; } // True = "Zwiększaj nadpłatę wraz ze zmniejszeniem raty"
        public DateTime CalcDataStart { get; set; } = DateTime.Today;
        public DateTime? CalcDataKoniec { get; set; }
    }
}
