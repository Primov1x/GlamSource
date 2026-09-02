using System.Text;
using GlamSource.Core;
using Lumina.Excel.Sheets;
var gd=new Lumina.GameData(@"D:\FF\game\sqpack",null);
var svc=new ItemDetailService(gd);
var items=gd.GetExcelSheet<Item>()!;
var sb=new StringBuilder("id\tname\tuicat\tsearchcat\tilvl\trarity\tglam\tequipslot\ticon\n");
int total=0,none=0; var byCat=new Dictionary<string,int>();
var sw=System.Diagnostics.Stopwatch.StartNew();
foreach(var it in items){
  var name=it.Name.ToString(); if(name.Length==0||it.RowId==0) continue;
  total++;
  ItemDetail? d; try{ d=svc.GetDetail(it.RowId);}catch(Exception e){ Console.Error.WriteLine($"ERR {it.RowId} {name}: {e.Message}"); continue;}
  if(d==null||!d.Sources.Any(x=>x.Description.StartsWith("No known current source"))) continue;
  none++;
  var cat=it.ItemUICategory.IsValid?it.ItemUICategory.Value.Name.ToString():"?";
  byCat[cat]=byCat.GetValueOrDefault(cat)+1;
  sb.Append($"{it.RowId}\t{name}\t{cat}\t{it.ItemSearchCategory.RowId}\t{it.LevelItem.RowId}\t{it.Rarity}\t{it.IsGlamorous}\t{it.EquipSlotCategory.RowId}\t{it.Icon}\n");
  if(total%5000==0) Console.Error.WriteLine($"{total} scanned, {none} without source, {sw.Elapsed.TotalSeconds:F0}s");
}
File.WriteAllText("nosource.tsv",sb.ToString());
Console.WriteLine($"TOTAL {total} NOSOURCE {none} ({100.0*none/total:F1}%) in {sw.Elapsed.TotalSeconds:F0}s");
foreach(var kv in byCat.OrderByDescending(k=>k.Value).Take(40)) Console.WriteLine($"{kv.Value,6}  {kv.Key}");
