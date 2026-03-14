using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DebtManager.Data;
using DebtManager.Models;

using System.Text;
namespace DebtManager.Controllers
{
    [Route("calculator")]
    public class CalculatorController : Controller
    {
        private readonly ApplicationDbContext _context;
        public CalculatorController(ApplicationDbContext context) 
        {
            _context = context;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int? id, int? editRataId, int? editNadplataId, int? editZmianaId, bool showOverpaymentModal = false)
        {
            var vm = new DebtDashboardViewModel();
            vm.ListaDlugow = await _context.Dlugi.ToListAsync();
            
            vm.EdytowanaRataId = editRataId;
            vm.EdytowanaNadplataId = editNadplataId;
            vm.EdytowanaZmianaId = editZmianaId;

            ViewBag.ShowOverpaymentModal = showOverpaymentModal;

            if (id.HasValue)
            {
                vm.WybranyDlug = await _context.Dlugi
                    .Include(d => d.Raty)
                    .Include(d => d.Nadplaty)
                    .Include(d => d.ZmianyOprocentowania)
                    .FirstOrDefaultAsync(d => d.Id == id.Value);

                if (vm.WybranyDlug != null)
                {
                    vm.KonfiguracjaDlugu = vm.WybranyDlug; // Do formularza edycji
                    
                    // Budujemy oś czasu (zawiera wszystko: raty, nadpłaty, zmiany)
                    BudujOsCzasu(vm);
                }
            }

            return View(vm);
        }

        [HttpPost("generuj-symulacje")]
        public async Task<IActionResult> GenerujSymulacje(int dlugId, string kwotaMiesieczna, bool stalaRataLaczna, DateTime dataStart, DateTime? dataKoniec)
        {
            decimal kwotaVal = ParseDecimal(kwotaMiesieczna);
            var dlug = await _context.Dlugi.FindAsync(dlugId);
            if (dlug == null) return RedirectToAction(nameof(Index));

            var stareNadplaty = await _context.Nadplaty
                .Where(n => n.DlugId == dlugId)
                .ToListAsync();
            _context.Nadplaty.RemoveRange(stareNadplaty);
            await _context.SaveChangesAsync(); // Zapisz usunięcie przed generowaniem

            if (kwotaVal <= 0) 
            {
                await PrzeliczHarmonogram(dlugId);
                return RedirectToAction(nameof(Index), new { id = dlugId });
            }

            // 3. Generuj nadpłaty
            // Potrzebujemy "Base Rata" (bez nadpłat) żeby wyliczyć różnicę dla "Stała Rata Łączna"
            // Ale to trudne bez pełnej symulacji. 
            // Uproszczenie dla "Stała Rata Łączna":
            // Bierzemy kwotę nadpłaty jako start. W każdym miesiącu sprawdzamy o ile spadła rata względem pierwotnej (lub poprzedniej) i dodajemy do nadpłaty.
            // To wymaga symulacji krok po kroku. Najlepiej zrobić to generując nadpłaty "na sztywno" co miesiąc.
            
            // Dla uproszczenia w kontrolerze: Generujemy nadpłaty "Stałe" lub "Smart" (rosnące).
            // Wariant Smart wymagałby pętli: Dodaj nadpłatę -> Przelicz harmonogram -> Sprawdź nową ratę -> Ustal kolejną nadpłatę.
            // To jest kosztowne obliczeniowo przy wielu miesiącach.
            // Zróbmy wersję optymistyczną: Generujemy nadpłaty stałe co miesiąc.
            // A flagę "stalaRataLaczna" obsłużymy w PrzeliczHarmonogram (logika dynamiczna).
            
            // Jednak PrzeliczHarmonogram czyta Nadpłaty z bazy.
            // Zróbmy tak: Dodajmy serię nadpłat. Jeśli "stalaRataLaczna" jest true, oznaczmy te nadpłaty specjalnym typem lub obsłużmy to generując je inteligentnie.
            
            // Prostsze podejście: Generujemy 60 nadpłat (lub do końca kredytu)
            // Jeśli stalaRataLaczna -> nie wiemy ile dokładnie wyjdzie bez przeliczeń.
            // Zróbmy prosto: Generujemy stałe kwoty nadpłat. Efekt kuli śnieżnej użytkownik zobaczy, jeśli wybierze typ rat "Stałe" i nadpłatę "Skrócenie Okresu" - wtedy rata maleje? Nie, przy stałych rata jest stała.
            // Przy "Stała Rata Łączna" użytkownik chce płacić np. zawsze 3000 zł, mimo że bank chce 2500, potem 2400.
            // To oznacza Nadpłatę = (3000 - RataWymagana).
            
            // Implementacja: W PrzeliczHarmonogram dodamy logikę "Wirtualnej Nadpłaty Wyrównawczej" dla planu,
            // LUB tutaj wygenerujemy konkretne wpisy. Wygenerowanie wpisów jest lepsze dla wykresu.
            
            // Pobierz aktualny stan długu (żeby wiedzieć do kiedy generować)
            // Uproszczenie: generujemy na max 30 lat lub do dataKoniec
            
            var limitDaty = dataKoniec ?? dataStart.AddYears(30);
            var cursor = dataStart;

            await PrzeliczHarmonogram(dlugId); 

            // Pobierz te wygenerowane raty
            var baseRaty = await _context.Raty
                .Where(r => r.DlugId == dlugId && r.DataRaty >= dataStart)
                .OrderBy(r => r.NumerRaty)
                .AsNoTracking()
                .ToListAsync();

            decimal targetTotalPayment = 0;
            if (stalaRataLaczna && baseRaty.Any())
            {
                // Target = Pierwsza rata z okresu + zadeklarowana nadpłata
                targetTotalPayment = baseRaty.First().Calkowita + kwotaVal;
            }

            var noweNadplaty = new List<Nadplata>();
            
            // Symulacja iteracyjna (uproszczona - zakładamy liniowy wpływ na kapitał, 
            // w rzeczywistości każda nadpłata zmienia odsetki w kolejnych ratach, więc baseRaty przestają być aktualne).
            // Prawdziwa symulacja wymagałaby pętli: Dodaj 1 nadpłatę -> Przelicz całość -> Pobierz nową ratę -> Dodaj kolejną...
            // To może być za wolne.
            
            // SZYBKI SPOSÓB: Generujemy stałe nadpłaty. Jeśli user chce "Smart", to generujemy nadpłaty rosnące szacunkowo.
            // Ale user może edytować ręcznie.
            // Zróbmy stałe nadpłaty. Jeśli user zaznaczył "Stała Rata Łączna", to znaczy że chce płacić stałą kwotę X.
            // Wtedy Nadpłata[i] = X - Rata[i].
            // Ponieważ Rata[i] maleje dzięki nadpłatom, Nadpłata[i] rośnie.
            // Musimy to robić iteracyjnie w pamięci.
            
            // ZROBIMY TO W PrzeliczHarmonogram. Tutaj tylko zapiszemy "Intencję" lub wygenerujemy jedną "Definicję Symulacji".
            // Ale system opiera się na rekordach.
            
            // Kompromis: Generujemy stałe nadpłaty. Efekt "Smart" dodamy w przyszłości lub zrobimy prostą aproksymację.
            // Na razie: Generujemy stałe kwoty co miesiąc.
            
            while (cursor <= limitDaty)
            {
                noweNadplaty.Add(new Nadplata
                {
                    DlugId = dlugId,
                    Data = cursor,
                    Kwota = kwotaVal,
                    Typ = TypNadplaty.Kapital, // Najkorzystniejsza
                    Efekt = EfektNadplaty.SkrocenieOkresu // Domyślnie
                });
                cursor = cursor.AddMonths(1);
                
                // Zabezpieczenie przed nieskończonością (np. 360 rat max)
                if (noweNadplaty.Count > 360) break;
            }

            _context.Nadplaty.AddRange(noweNadplaty);
            await _context.SaveChangesAsync();

            // Teraz przelicz, uwzględniając te nowe nadpłaty
            await PrzeliczHarmonogram(dlugId);

            return RedirectToAction(nameof(Index), new { id = dlugId });
        }

        [HttpPost("zapisz")]
        public async Task<IActionResult> ZapiszDlug(
            [Bind(Prefix = "KonfiguracjaDlugu")] Dlug model, string action)
        {
            // 1. TRYB SYMULACJI (W PAMIĘCI)
            if (action == "symuluj")
            {
                var vm = new DebtDashboardViewModel();
                vm.ListaDlugow = await _context.Dlugi.ToListAsync();
                
                // Ustawiamy model symulowany jako "WybranyDlug"
                vm.WybranyDlug = model;
                vm.KonfiguracjaDlugu = model;
                vm.CzySymulacja = true;

                // Generujemy harmonogram w pamięci (na obiekcie model)
                GenerujHarmonogramInMemory(model);
                BudujOsCzasu(vm);

                return View("Index", vm);
            }

            // Fix: Jeśli binding daty zawiódł (min value), spróbujmy ustawić dzisiejszą, 
            // ale nie nadpisujmy jeśli użytkownik podał poprawną datę z przeszłości.
            // Usunięcie inicjalizatora w modelu zapobiega "wymuszaniu" daty przyszłej,
            // a tutaj zabezpieczamy się przed pustą datą.
            if (model.DataPierwszejRaty == default) model.DataPierwszejRaty = DateTime.Today;

            // 2. TRYB ZAPISU DO BAZY
            if (model.Id == 0)
            {
                _context.Dlugi.Add(model);

                await _context.SaveChangesAsync();
                await PrzeliczHarmonogram(model.Id);
            }
            else
            {
                var istniejacy = await _context.Dlugi.FindAsync(model.Id);
                if (istniejacy != null)
                {
                    _context.Entry(istniejacy).CurrentValues.SetValues(model);
                    
                    await _context.SaveChangesAsync();
                    await PrzeliczHarmonogram(model.Id);
                }
            }

            return RedirectToAction(nameof(Index), new { id = model.Id });
        }


        [HttpPost("usun-dlug")]
        public async Task<IActionResult> UsunDlug(int id)
        {
            var dlug = await _context.Dlugi.FindAsync(id);
            if (dlug != null)
            {
                _context.Dlugi.Remove(dlug);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("edytuj-rate")]
        public async Task<IActionResult> EdytujRate(int rataId, string kapital, string odsetki, bool przelicz)
        {
            decimal kapVal = ParseDecimal(kapital);
            decimal odsVal = ParseDecimal(odsetki);
            var rata = await _context.Raty.FindAsync(rataId);
            // Zabezpieczenie: nie edytujemy opłaconych
            if (rata != null && rata.CzyOplacona) return RedirectToAction(nameof(Index), new { id = rata.DlugId });

            if (rata != null)
            {
                rata.Kapital = kapVal;
                rata.Odsetki = odsVal;
                rata.CzyEdytowanaRecznie = true;
                await _context.SaveChangesAsync();

                if (przelicz)
                {
                    await PrzeliczHarmonogram(rata.DlugId);
                }
            }
            return RedirectToAction(nameof(Index), new { id = rata?.DlugId });
        }

        [HttpPost("dodaj-nadplate")]
        public async Task<IActionResult> DodajNadplate(int dlugId, DateTime data, string kwota, string kapital, string czescOdsetkowa, TypNadplaty typ, EfektNadplaty efekt)
        {
            if (dlugId == 0) return RedirectToAction(nameof(Index));

            decimal kwotaVal = ParseDecimal(kwota);
            decimal kapVal = ParseDecimal(kapital);
            decimal odsetkiVal = ParseDecimal(czescOdsetkowa);
            var dlug = await _context.Dlugi.FindAsync(dlugId);
            if (dlug == null) return RedirectToAction(nameof(Index));

            if (typ == TypNadplaty.Reczna)
            {
                kwotaVal = kapVal + odsetkiVal; // Cała kwota to kapitał + odsetki
            }

            var nadplata = new Nadplata { DlugId = dlugId, Data = data, Kwota = kwotaVal, CzescOdsetkowa = (typ == TypNadplaty.Reczna) ? odsetkiVal : 0, Typ = typ, Efekt = efekt, CzyEdytowanaRecznie = (typ == TypNadplaty.Reczna) };

            _context.Nadplaty.Add(nadplata);
            await _context.SaveChangesAsync();
            await PrzeliczHarmonogram(dlugId);
            return RedirectToAction(nameof(Index), new { id = dlugId });
        }

        [HttpPost("dodaj-zmiane-oprocentowania")]
        public async Task<IActionResult> DodajZmianeOprocentowania(int DlugId, DateTime DataZmiany, string NoweOprocentowanie)
        {
            if (DlugId == 0) return RedirectToAction(nameof(Index));

            decimal val = ParseDecimal(NoweOprocentowanie);
            var zmiana = new ZmianaOprocentowania { DlugId = DlugId, DataZmiany = DataZmiany, NoweOprocentowanie = val };
            _context.ZmianyOprocentowania.Add(zmiana);
            await _context.SaveChangesAsync();
            await PrzeliczHarmonogram(zmiana.DlugId);
            return RedirectToAction(nameof(Index), new { id = zmiana.DlugId });
        }

        [HttpPost("usun-zdarzenie")]
        public async Task<IActionResult> UsunZdarzenie(int id, string typ)
        {
            int dlugId = 0;
            if (typ == "nadplata")
            {
                var n = await _context.Nadplaty.FindAsync(id);
                if (n != null) 
                { 
                    dlugId = n.DlugId; 
                    _context.Nadplaty.Remove(n); 
                }
            }
            else if (typ == "zmiana")
            {
                var z = await _context.ZmianyOprocentowania.FindAsync(id);
                if (z != null) { dlugId = z.DlugId; _context.ZmianyOprocentowania.Remove(z); }
            }
            await _context.SaveChangesAsync();
            if (dlugId > 0) await PrzeliczHarmonogram(dlugId);
            return RedirectToAction(nameof(Index), new { id = dlugId });
        }

        [HttpPost("edytuj-zmiane")]
        public async Task<IActionResult> EdytujZmiane(int Id, DateTime DataZmiany, string NoweOprocentowanie)
        {
            decimal val = ParseDecimal(NoweOprocentowanie);
            var z = await _context.ZmianyOprocentowania.FindAsync(Id);
            if (z != null) { 
                z.DataZmiany = DataZmiany; 
                z.NoweOprocentowanie = val; 
                await _context.SaveChangesAsync(); 
                await PrzeliczHarmonogram(z.DlugId); 
                return RedirectToAction(nameof(Index), new { id = z.DlugId }); 
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost("edytuj-nadplate")]
        public async Task<IActionResult> EdytujNadplate(int Id, DateTime Data, string kapital, string odsetki, TypNadplaty Typ, EfektNadplaty Efekt)
        {
            var n = await _context.Nadplaty.FindAsync(Id);
            if (n != null)
            {
                decimal kapVal = ParseDecimal(kapital);
                decimal odsVal = ParseDecimal(odsetki);

                n.Data = Data;
                n.Kwota = kapVal + odsVal; // Cała kwota to kapitał + odsetki
                n.CzescOdsetkowa = odsVal;
                n.Typ = Typ;
                n.Efekt = Efekt;
                n.CzyEdytowanaRecznie = (Typ == TypNadplaty.Reczna) ? true : n.CzyEdytowanaRecznie;
                await _context.SaveChangesAsync();
                await PrzeliczHarmonogram(n.DlugId);
                return RedirectToAction(nameof(Index), new { id = n.DlugId });
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("export")]
        public async Task<IActionResult> ExportCsv()
        {
            var debts = await _context.Dlugi.AsNoTracking().ToListAsync();
            var sb = new StringBuilder();
            sb.AppendLine("Id;Nazwa;WartoscPoczatkowa;Oprocentowanie;LiczbaRat;DataPierwszejRaty;TypRat;Currency;ExchangeRate");

            foreach (var d in debts)
            {
                sb.AppendLine($"{d.Id};{d.Nazwa};{d.WartoscPoczatkowa.ToString(System.Globalization.CultureInfo.InvariantCulture)};{d.Oprocentowanie.ToString(System.Globalization.CultureInfo.InvariantCulture)};{d.LiczbaRat};{d.DataPierwszejRaty:yyyy-MM-dd};{d.TypRat};{d.Currency};{d.ExchangeRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "debts.csv");
        }

        [HttpPost("import")]
        public async Task<IActionResult> ImportCsv(IFormFile file)
        {
            if (file == null || file.Length == 0) return RedirectToAction(nameof(Index));

            var newDebts = new List<Dlug>();
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                await reader.ReadLineAsync(); // Pomiń nagłówek
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = line.Split(';');
                    if (parts.Length < 10) continue;

                    try
                    {
                        var debt = new Dlug
                        {
                            Nazwa = parts[1],
                            WartoscPoczatkowa = decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            Oprocentowanie = decimal.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                            LiczbaRat = int.Parse(parts[4]),
                            DataPierwszejRaty = DateTime.Parse(parts[5]),
                            TypRat = Enum.Parse<TypRat>(parts[6]),
                            Currency = parts.Length > 7 ? parts[7] : "PLN",
                            ExchangeRate = parts.Length > 8 ? decimal.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture) : 1m
                        };
                        _context.Dlugi.Add(debt);
                        newDebts.Add(debt);
                    }
                    catch { /* Pomiń błędne linie */ }
                }
            }
            await _context.SaveChangesAsync();
            foreach (var debt in newDebts) await PrzeliczHarmonogram(debt.Id);
            return RedirectToAction(nameof(Index));
        }

        [HttpGet("export-installments")]
        public async Task<IActionResult> ExportInstallmentsCsv(int debtId)
        {
            var dlug = await _context.Dlugi
                .Include(d => d.Raty)
                .Include(d => d.Nadplaty)
                .Include(d => d.ZmianyOprocentowania)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == debtId);

            if (dlug == null) return NotFound();

            var raty = dlug.Raty.ToList();
            var nadplaty = dlug.Nadplaty.ToList();
            var zmiany = dlug.ZmianyOprocentowania.ToList();

            var osCzasu = new List<ElementOsiCzasu>();
            foreach (var r in raty) osCzasu.Add(new ElementOsiCzasu { Data = r.DataRaty, TypWiersza = "Rata", Rata = r });
            foreach (var n in nadplaty) osCzasu.Add(new ElementOsiCzasu { Data = n.Data, TypWiersza = "Nadplata", Nadplata = n });
            foreach (var z in zmiany) osCzasu.Add(new ElementOsiCzasu { Data = z.DataZmiany, TypWiersza = "ZmianaOprocentowania", ZmianaOprocentowania = z });
            
            osCzasu = osCzasu.OrderBy(x => x.Data).ThenBy(x => x.TypWiersza == "Rata" ? 1 : 0).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("Data;Typ;KwotaCalkowita;Kapital;Odsetki;NoweOprocentowanie;EfektNadplaty;NumerRaty;CzyOplacona;TypNadplaty");

            foreach (var item in osCzasu)
            {
                if (item.Rata != null)
                {
                    sb.AppendLine($"{item.Rata.DataRaty:yyyy-MM-dd};Rata;{item.Rata.Calkowita.ToString(System.Globalization.CultureInfo.InvariantCulture)};{item.Rata.Kapital.ToString(System.Globalization.CultureInfo.InvariantCulture)};{item.Rata.Odsetki.ToString(System.Globalization.CultureInfo.InvariantCulture)};;;{item.Rata.NumerRaty};{(item.Rata.CzyOplacona ? "1" : "0")};");
                }
                else if (item.Nadplata != null)
                {
                    var kapitalNadplaty = item.Nadplata.Kwota - item.Nadplata.CzescOdsetkowa;
                    sb.AppendLine($"{item.Nadplata.Data:yyyy-MM-dd};Nadplata;{item.Nadplata.Kwota.ToString(System.Globalization.CultureInfo.InvariantCulture)};{kapitalNadplaty.ToString(System.Globalization.CultureInfo.InvariantCulture)};{item.Nadplata.CzescOdsetkowa.ToString(System.Globalization.CultureInfo.InvariantCulture)};;{item.Nadplata.Efekt};;;{item.Nadplata.Typ}");
                }
                else if (item.ZmianaOprocentowania != null)
                {
                    sb.AppendLine($"{item.ZmianaOprocentowania.DataZmiany:yyyy-MM-dd};Zmiana;;;;{item.ZmianaOprocentowania.NoweOprocentowanie.ToString(System.Globalization.CultureInfo.InvariantCulture)};;;;");
                }
            }

            var fileName = $"harmonogram_{debtId}.csv";
            return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", fileName);
        }

        [HttpPost("import-installments")]
        public async Task<IActionResult> ImportInstallmentsCsv(int debtId, IFormFile file)
        {
            if (file == null || file.Length == 0) return RedirectToAction(nameof(Index), new { id = debtId });

            var newRaty = new List<Rata>();
            var newNadplaty = new List<Nadplata>();
            var newZmiany = new List<ZmianaOprocentowania>();

            using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
            {
                var header = await reader.ReadLineAsync(); // Pomiń nagłówek
                bool isNewFormat = header != null && header.Contains("Typ"); // Data;Typ;KwotaCalkowita;...
                
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(';');
                    if (parts.Length < 4) continue;

                    try
                    {
                        if (isNewFormat)
                        {
                            // 0:Data, 1:Typ, 2:KwotaCalkowita, 3:Kapital, 4:Odsetki, 5:NoweOprocentowanie, 6:EfektNadplaty, 7:NumerRaty, 8:CzyOplacona, 9:TypNadplaty
                            var typWiersza = parts[1];
                            if (typWiersza == "Rata")
                            {
                                var rata = new Rata
                                {
                                    DlugId = debtId,
                                    NumerRaty = int.Parse(parts[7]),
                                    DataRaty = DateTime.Parse(parts[0]),
                                    Kapital = decimal.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                                    Odsetki = decimal.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                                    CzyOplacona = parts.Length > 8 && (parts[8] == "1" || parts[8].ToLower() == "true"),
                                    CzyEdytowanaRecznie = true
                                };
                                newRaty.Add(rata);
                            }
                            else if (typWiersza == "Nadplata")
                            {
                                var nadplata = new Nadplata
                                {
                                    DlugId = debtId,
                                    Data = DateTime.Parse(parts[0]),
                                    Kwota = decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                                    CzescOdsetkowa = decimal.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture),
                                    Efekt = Enum.TryParse<EfektNadplaty>(parts[6], out var e) ? e : EfektNadplaty.ObnizenieRat,
                                    Typ = parts.Length > 9 && Enum.TryParse<TypNadplaty>(parts[9], out var t) ? t : TypNadplaty.Reczna,
                                    CzyEdytowanaRecznie = true
                                };
                                newNadplaty.Add(nadplata);
                            }
                            else if (typWiersza == "Zmiana")
                            {
                                var zmiana = new ZmianaOprocentowania
                                {
                                    DlugId = debtId,
                                    DataZmiany = DateTime.Parse(parts[0]),
                                    NoweOprocentowanie = decimal.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture)
                                };
                                newZmiany.Add(zmiana);
                            }
                        }
                        else
                        {
                            // Stary format (NumerRaty;DataRaty;Kapital;Odsetki;CzyOplacona)
                            var rata = new Rata
                            {
                                DlugId = debtId,
                                NumerRaty = int.Parse(parts[0]),
                                DataRaty = DateTime.Parse(parts[1]),
                                Kapital = decimal.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                                Odsetki = decimal.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture),
                                CzyOplacona = parts.Length > 4 && (parts[4] == "1" || parts[4].ToLower() == "true"),
                                CzyEdytowanaRecznie = true // Import traktujemy jako manualny override
                            };
                            newRaty.Add(rata);
                        }
                    }
                    catch { /* ignore bad lines */ }
                }
            }

            if (newRaty.Any() || newNadplaty.Any() || newZmiany.Any())
            {
                var stareRaty = await _context.Raty.Where(r => r.DlugId == debtId).ToListAsync();
                _context.Raty.RemoveRange(stareRaty);
                if (newRaty.Any()) _context.Raty.AddRange(newRaty);

                var stareNadplaty = await _context.Nadplaty.Where(n => n.DlugId == debtId).ToListAsync();
                _context.Nadplaty.RemoveRange(stareNadplaty);
                if (newNadplaty.Any()) _context.Nadplaty.AddRange(newNadplaty);

                var stareZmiany = await _context.ZmianyOprocentowania.Where(z => z.DlugId == debtId).ToListAsync();
                _context.ZmianyOprocentowania.RemoveRange(stareZmiany);
                if (newZmiany.Any()) _context.ZmianyOprocentowania.AddRange(newZmiany);
                
                await _context.SaveChangesAsync();
                
                await PrzeliczHarmonogram(debtId);
            }

            return RedirectToAction(nameof(Index), new { id = debtId });
        }

        [HttpPost("bulk-delete")]
        public async Task<IActionResult> BulkDelete(List<int> ids)
        {
            if (ids == null || !ids.Any()) return RedirectToAction(nameof(Index));

            var debts = await _context.Dlugi.Where(d => ids.Contains(d.Id)).ToListAsync();
            _context.Dlugi.RemoveRange(debts);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        [HttpGet("get-debt-info")]
        public async Task<IActionResult> GetDebtInfo(int debtId, int? installmentNumber)
        {
            var debt = await _context.Dlugi
                .Include(d => d.Raty)
                .FirstOrDefaultAsync(d => d.Id == debtId);

            if (debt == null) return NotFound();

            Rata? targetRata = null;

            if (installmentNumber.HasValue)
            {
                targetRata = debt.Raty.FirstOrDefault(r => r.NumerRaty == installmentNumber.Value);
            }
            else
            {
                targetRata = debt.Raty
                    .Where(r => !r.CzyOplacona)
                    .OrderBy(r => r.NumerRaty)
                    .FirstOrDefault();
            }

            if (targetRata == null) return Json(new { message = "Nie znaleziono raty." });

            return Json(targetRata);
        }

        // --- LOGIKA BIZNESOWA ---

        private void BudujOsCzasu(DebtDashboardViewModel vm)
        {
            var dlug = vm.WybranyDlug;
            if (dlug == null) return;

            // Dodaj raty
            foreach (var r in dlug.Raty)
                vm.OsCzasu.Add(new ElementOsiCzasu { Data = r.DataRaty, TypWiersza = "Rata", Rata = r });

            // Dodaj nadpłaty
            foreach (var n in dlug.Nadplaty)
                vm.OsCzasu.Add(new ElementOsiCzasu { Data = n.Data, TypWiersza = "Nadplata", Nadplata = n });

            // Dodaj zmiany %
            foreach (var z in dlug.ZmianyOprocentowania)
                vm.OsCzasu.Add(new ElementOsiCzasu { Data = z.DataZmiany, TypWiersza = "ZmianaOprocentowania", ZmianaOprocentowania = z });

            // Sortuj chronologicznie
            vm.OsCzasu = vm.OsCzasu.OrderBy(x => x.Data).ThenBy(x => x.TypWiersza == "Rata" ? 1 : 0).ToList();
        }


        // Logika obliczeń wyciągnięta do metody statycznej/prywatnej działającej na obiekcie
        private void GenerujHarmonogramInMemory(Dlug dlug)
        {
            // Czyścimy raty (w pamięci)
            dlug.Raty.Clear();

            decimal pozostalyKapital = dlug.WartoscPoczatkowa;
            DateTime dataBiezaca = dlug.DataPierwszejRaty;
            decimal aktualneOprocentowanie = dlug.Oprocentowanie;
            int liczbaRat = dlug.LiczbaRat;

            for (int numerRaty = 1; numerRaty <= liczbaRat; numerRaty++)
            {
                // Uproszczona symulacja (bez nadpłat i zmian %, bo to nowy dług)
                decimal odsetki = Math.Round(pozostalyKapital * (aktualneOprocentowanie / 100m) / 12m, 2);
                decimal kapital;

                if (dlug.TypRat == TypRat.Stale)
                {
                    int pozostaloRat = liczbaRat - numerRaty + 1;
                    decimal r = (aktualneOprocentowanie / 100m) / 12m;
                    if (r > 0)
                    {
                        double r_double = (double)r;
                        double pmt = (double)pozostalyKapital * r_double / (1 - Math.Pow(1 + r_double, -pozostaloRat));
                        kapital = (decimal)pmt - odsetki;
                    }
                    else kapital = pozostalyKapital / pozostaloRat;
                }
                else // Malejące
                {
                    int pozostaloRat = liczbaRat - numerRaty + 1;
                    kapital = pozostalyKapital / pozostaloRat;
                }

                kapital = Math.Round(kapital, 2);
                if (kapital > pozostalyKapital || numerRaty == liczbaRat) kapital = pozostalyKapital;

                var rata = new Rata
                {
                    NumerRaty = numerRaty,
                    DataRaty = dataBiezaca,
                    OprocentowanieRaty = aktualneOprocentowanie,
                    Odsetki = odsetki,
                    Kapital = kapital,
                    PozostaloKapitalu = pozostalyKapital - kapital
                };
                dlug.Raty.Add(rata);

                pozostalyKapital -= kapital;
                dataBiezaca = dataBiezaca.AddMonths(1);
                if (pozostalyKapital <= 0) break;
            }
        }

        // Kluczowa zmiana: obsługa wariantu
        private async Task PrzeliczHarmonogram(int dlugId)
        {
            var dlug = await _context.Dlugi
                .Include(d => d.Raty)
                .Include(d => d.Nadplaty)
                .Include(d => d.ZmianyOprocentowania)
                .FirstOrDefaultAsync(d => d.Id == dlugId);

            if (dlug == null) return;

            var ratyWariantu = dlug.Raty.ToList();
            
            var zablokowaneRaty = ratyWariantu.Where(r => r.CzyEdytowanaRecznie || r.CzyOplacona).ToDictionary(r => r.NumerRaty);
            
            // Wyczyść stare raty (te automatyczne i nieopłacone)
            var doUsuniecia = ratyWariantu.Where(r => !r.CzyEdytowanaRecznie && !r.CzyOplacona).ToList();
            _context.Raty.RemoveRange(doUsuniecia);

            // 2. Inicjalizacja zmiennych symulacji
            decimal pozostalyKapital = dlug.WartoscPoczatkowa;
            DateTime dataBiezaca = dlug.DataPierwszejRaty;
            DateTime dataPoprzednia = dataBiezaca.AddMonths(-1); // Początek pierwszego okresu
            
            decimal aktualneOprocentowanie = dlug.Oprocentowanie;
            var zmianyOprocentowania = dlug.ZmianyOprocentowania
                .OrderBy(z => z.DataZmiany).ToList();

            var startoweZmiany = zmianyOprocentowania.Where(z => z.DataZmiany <= dataPoprzednia).ToList();
            if (startoweZmiany.Any()) aktualneOprocentowanie = startoweZmiany.Last().NoweOprocentowanie;

            int liczbaRat = dlug.LiczbaRat;
            int numerRaty = 1;

            // Sortowanie zdarzeń
            var nadplaty = dlug.Nadplaty
                .OrderBy(n => n.Data).ToList();

            // Obliczenie bazowej raty (Annuity) dla całego okresu - potrzebne do logiki "Skrócenie Okresu"
            // PMT = P * r / (1 - (1+r)^-n)
            decimal rBase = (dlug.Oprocentowanie / 100m) / 12m;
            decimal basePMT = 0;
            if (rBase > 0)
            {
                double r_double = (double)rBase;
                double pmt_double = (double)dlug.WartoscPoczatkowa * r_double / (1 - Math.Pow(1 + r_double, -dlug.LiczbaRat));
                basePMT = (decimal)pmt_double;
            }
            else basePMT = dlug.WartoscPoczatkowa / dlug.LiczbaRat;

            List<Rata> noweRaty = new List<Rata>();

            // Pętla generowania rat
            while (pozostalyKapital > 0.01m && numerRaty <= liczbaRat + 100) // +100 jako bezpiecznik
            {
                decimal nadplaconeOdsetkiWtymOkresie = 0;

                // A. Obsługa nadpłat w tym okresie
                var nadplatyWtymOkresie = nadplaty.Where(n => n.Data <= dataBiezaca && n.Data > dataPoprzednia).ToList();
                foreach(var n in nadplatyWtymOkresie)
                {
                    // Jeśli nie jest edytowana ręcznie, przelicz część odsetkową.
                    // W przeciwnym razie, użyj wartości już zapisanej w bazie.
                    if (!n.CzyEdytowanaRecznie && n.Typ != TypNadplaty.Reczna)
                    {
                        decimal czescOdsetkowa = 0;
                        if (n.Typ == TypNadplaty.Proporcjonalnie)
                        {
                            // Szacujemy odsetki narosłe od początku okresu do dnia nadpłaty (precyzyjniej niż wcześniej)
                            int dniOdPoczatku = (n.Data - dataPoprzednia).Days;
                            decimal odsetkiNarosle = pozostalyKapital * (aktualneOprocentowanie / 100m) * dniOdPoczatku / 365m;
                            czescOdsetkowa = Math.Round(odsetkiNarosle, 2);
                            
                            if (czescOdsetkowa > n.Kwota) czescOdsetkowa = n.Kwota;
                        }
                        n.CzescOdsetkowa = czescOdsetkowa;
                    }

                    nadplaconeOdsetkiWtymOkresie += n.CzescOdsetkowa;

                    decimal czescKapitalowa = n.Kwota - n.CzescOdsetkowa;
                    
                    pozostalyKapital -= czescKapitalowa;

                    // Jeśli nadpłata była "Skróceniem Okresu", to nie zmieniamy harmonogramu (liczby rat w parametrach),
                    // ale algorytm poniżej przy wyliczaniu raty musi wiedzieć, żeby trzymać wysokość raty, a nie przeliczać na nowo.
                    // W tym miejscu tylko aktualizujemy kapitał.
                    if (n.Efekt == EfektNadplaty.SkrocenieOkresu) {
                        // Logika obsłużona przy wyliczaniu raty
                    }
                }

                if (pozostalyKapital <= 0) break;

                // B. Wylicz ratę (Odsetki precyzyjnie z uwzględnieniem zmian w trakcie miesiąca)
                decimal noweOprocentowanie;
                decimal odsetkiCalkowite;

                bool bylaZmiana = zmianyOprocentowania.Any(z => z.DataZmiany > dataPoprzednia && z.DataZmiany <= dataBiezaca);

                if (bylaZmiana)
                {
                    odsetkiCalkowite = ObliczOdsetkiPrecyzyjnie(pozostalyKapital, dataPoprzednia, dataBiezaca, aktualneOprocentowanie, zmianyOprocentowania, out noweOprocentowanie);
                }
                else
                {
                    odsetkiCalkowite = Math.Round(pozostalyKapital * (aktualneOprocentowanie / 100m) / 12m, 2);
                    noweOprocentowanie = aktualneOprocentowanie;
                }

                // Pomniejsz odsetki raty o to, co już zapłacono w nadpłatach
                decimal odsetkiDoZaplaty = odsetkiCalkowite - nadplaconeOdsetkiWtymOkresie;
                if (odsetkiDoZaplaty < 0) odsetkiDoZaplaty = 0;

                // Aktualizuj oprocentowanie na przyszłość (jeśli zmieniło się w trakcie miesiąca, to na koniec miesiąca obowiązuje nowe)
                aktualneOprocentowanie = noweOprocentowanie;

                Rata rata;
                if (zablokowaneRaty.ContainsKey(numerRaty))
                {
                    // Użyj zablokowanej
                    rata = zablokowaneRaty[numerRaty]; 
                    // Nie modyfikujemy jej wartości, tylko bierzemy pod uwagę, że kapitał zmalał o jej część kapitałową
                    // Ale uwaga: jeśli rata była opłacona/edytowana, to jej 'Kapitał' jest faktem historycznym.
                } 
                else
                {
                    // Wylicz nową
                    rata = new Rata
                    {
                        DlugId = dlug.Id,
                        NumerRaty = numerRaty,
                        DataRaty = dataBiezaca,
                        OprocentowanieRaty = aktualneOprocentowanie,
                        Odsetki = odsetkiDoZaplaty
                    };

                    // Oblicz kapitał
                    // Sprawdź czy mamy efekt skrócenia okresu (czy suma nadpłat typu Skrócenie > 0)
                    // Uproszczenie: sprawdzamy czy ostatnia nadpłata była skracająca, albo czy w ogóle taka była.
                    // Bardziej precyzyjnie: Jeśli celujemy w skrócenie okresu, staramy się utrzymać ratę całkowitą na poziomie basePMT.
                    
                    bool trybSkracania = nadplaty.Any(n => n.Efekt == EfektNadplaty.SkrocenieOkresu && n.Data <= dataBiezaca);

                    if (trybSkracania && dlug.TypRat == TypRat.Stale)
                    {
                        // W trybie skracania okresu, rata całkowita powinna być taka jak pierwotnie (basePMT),
                        // chyba że odsetki są wyższe (niemożliwe przy nadpłacie) lub kapitał się kończy.
                        decimal planowanaCalkowita = (decimal)basePMT;
                        rata.Kapital = planowanaCalkowita - rata.Odsetki;
                    }
                    else
                    {
                        // Standardowe przeliczanie (Obniżenie raty)
                        int pozostaloRat = liczbaRat - numerRaty + 1;
                        if (pozostaloRat <= 0) pozostaloRat = 1;

                        if (dlug.TypRat == TypRat.Stale)
                        {
                            decimal r = (aktualneOprocentowanie / 100m) / 12m;
                            if (r > 0)
                            {
                                double r_double = (double)r;
                                double pmt_double = (double)pozostalyKapital * r_double / (1 - Math.Pow(1 + r_double, -pozostaloRat));
                                rata.Kapital = (decimal)pmt_double - rata.Odsetki;
                            }
                            else rata.Kapital = pozostalyKapital / pozostaloRat;
                        }
                        else // Malejące
                        {
                            rata.Kapital = pozostalyKapital / pozostaloRat;
                        }
                    }

                    // Zaokrąglenia i ostatnia rata
                    rata.Kapital = Math.Round(rata.Kapital, 2);
                    if (rata.Kapital > pozostalyKapital || numerRaty == liczbaRat)
                    {
                        rata.Kapital = pozostalyKapital;
                    }

                    noweRaty.Add(rata);
                }

                pozostalyKapital -= rata.Kapital;
                rata.PozostaloKapitalu = pozostalyKapital;

                // Następny okres
                dataPoprzednia = dataBiezaca;
                dataBiezaca = dataBiezaca.AddMonths(1); // Uproszczenie: zawsze miesięcznie
                numerRaty++;
            }

            if (noweRaty.Any())
            {
                _context.Raty.AddRange(noweRaty);
            }
            
            await _context.SaveChangesAsync();
        }

        private decimal ObliczOdsetkiPrecyzyjnie(decimal kapital, DateTime dataOd, DateTime dataDo, decimal oprocentowanieStartowe, List<ZmianaOprocentowania> zmiany, out decimal oprocentowanieKoncowe)
        {
            decimal sumaOdsetek = 0;
            DateTime cursor = dataOd;
            decimal currentRate = oprocentowanieStartowe;
            
            // Znajdź zmiany w analizowanym okresie (dataOd, dataDo]
            var changes = zmiany.Where(z => z.DataZmiany > dataOd && z.DataZmiany <= dataDo).OrderBy(z => z.DataZmiany).ToList();

            foreach (var change in changes)
            {
                // Oblicz dni z poprzednim oprocentowaniem
                int days = (change.DataZmiany - cursor).Days;
                if (days > 0)
                {
                    sumaOdsetek += kapital * (currentRate / 100m) * days / 365m;
                }
                
                // Przesuń kursor i zmień stawkę
                cursor = change.DataZmiany;
                currentRate = change.NoweOprocentowanie;
            }

            // Oblicz dni od ostatniej zmiany (lub początku) do końca okresu
            int remainingDays = (dataDo - cursor).Days;
            if (remainingDays > 0)
            {
                sumaOdsetek += kapital * (currentRate / 100m) * remainingDays / 365m;
            }

            oprocentowanieKoncowe = currentRate;
            return Math.Round(sumaOdsetek, 2);
        }


        private decimal ParseDecimal(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Replace(",", ".");
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal result))
            {
                return result;
            }
            return 0;
        }
    }
}