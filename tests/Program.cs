using System; using System.IO; using System.IO.Compression; using System.Linq; using System.Text; using System.Collections.Generic;
using KeyFlip;
class Sim {
  public StringBuilder Screen = new StringBuilder(); public Lang Layout; public RunTracker T; public List<string> Log = new List<string>();
  public Sim(RunTracker t, Lang layout){T=t;Layout=layout;}
  // press the keys that would produce `text` if typed in `keysOf` layout; the app is actually in this.Layout
  public void Type(string text, Lang keysOf){
    foreach(char c in text){
      if(c=='\b'){ if(Screen.Length>0) Screen.Length--; T.Backspace(); continue; }
      if(c=='\x01'){ Apply(T.Manual()); continue; } // hotkey
      int vk; bool sh;
      if(c==' '){ vk=KeyMap.VK_SPACE; sh=false; }
      else if(c=='\n'){ vk=KeyMap.VK_RETURN; sh=false; }
      else if(!KeyMap.KeyFor(c,keysOf,out vk,out sh)) throw new Exception("no key for "+c);
      Press(vk,sh);
    }
  }
  public void Press(int vk,bool sh){
    if(vk==KeyMap.VK_SPACE){ var u=new Unit(UnitKind.Space,vk,false,Layout); var p=T.Boundary(u,false,true); if(p!=null) Apply(p); else Screen.Append(' '); return; }
    if(vk==KeyMap.VK_RETURN){ var u=new Unit(UnitKind.Neutral,vk,false,Layout); var p=T.Boundary(u,true,true); if(p!=null) Apply(p); else Screen.Append('\n'); return; }
    if(KeyMap.IsWordKey(vk,sh)){ var u=new Unit(UnitKind.Word,vk,sh,Layout); T.AddWordUnit(u); Screen.Append(u.CharIn(Layout)); return; }
    var n=new Unit(UnitKind.Neutral,vk,sh,Layout); var pl=T.Boundary(n,false,true); if(pl!=null) Apply(pl); else Screen.Append(n.CharIn(Layout));
  }
  public void Apply(FixPlan p){
    if(p==null) return;
    Log.Add(p.Kind+": "+p.Before+" -> "+p.After); if(p.LearnWord!=null) Program.Learn(p.LearnWord);
    Screen.Length-=p.Backspaces; Layout=p.SwitchTo;
    foreach(var u in p.Retype) Screen.Append(u.CharIn(Layout));
    if(p.Boundary!=null) Screen.Append(p.Boundary.Vk==KeyMap.VK_RETURN?'\n':p.Boundary.CharIn(Layout));
  }
}
class Program {
  public static Lexicon lx; static int fails=0; public static void Learn(string w){ lx.UserWords.Add(w); }
  static string Run(string[] steps, Lang start, out List<string> log){
    var s=new Sim(new RunTracker(lx,new Settings()),start);
    foreach(var st in steps){ Lang k = st.StartsWith("he:")?Lang.He:Lang.En; s.Type(st.Substring(3),k); }
    log=s.Log; return s.Screen.ToString();
  }
  static void Check(string name, Lang start, string expect, params string[] steps){
    lx.UserWords.Clear(); List<string> log; var got=Run(steps,start,out log);
    bool ok=got==expect; if(!ok) fails++;
    Console.WriteLine((ok?"PASS ":"FAIL ")+name+"  => ["+got+"]"+(ok?"":"  expected ["+expect+"]")+"  "+string.Join(" | ",log));
  }
  // The word lists ship with the app (src/Resources/*.txt.gz); find them from wherever the tests are run.
  static Stream Words(string name){
    foreach(var start in new[]{Directory.GetCurrentDirectory(),AppContext.BaseDirectory})
      for(var d=new DirectoryInfo(start);d!=null;d=d.Parent){
        string p=Path.Combine(d.FullName,"src","Resources",name);
        if(File.Exists(p)) return new GZipStream(File.OpenRead(p),CompressionMode.Decompress);
      }
    throw new FileNotFoundException("src/Resources/"+name+" not found - run the tests from inside the KeyFlip folder");
  }
  static IEnumerable<string> Lines(string name){ using(var r=new StreamReader(Words(name),Encoding.UTF8)){ string l; while((l=r.ReadLine())!=null) yield return l; } }
  static void Main(){
    lx=Lexicon.Load(Words("en.txt.gz"),Words("he.txt.gz"));
    // Scenarios: layout the app is in, expected screen, keys pressed (named by the text they'd give in that language)
    Check("Hebrew typed in English",Lang.En,"שלום מה שלומך ",  "he:שלום מה שלומך ");
    Check("English typed in Hebrew",Lang.He,"hello how are you ","en:hello how are you ");
    Check("Correct Hebrew untouched",Lang.He,"שלום לכולם ","he:שלום לכולם ");
    Check("Correct English untouched",Lang.En,"please send the report ","en:please send the report ");
    Check("Lookback ambiguous 'גם'",Lang.En,"גם אני ","he:גם אני ");
    Check("Capitalized English in Hebrew",Lang.He,"Hello team, ","en:Hello team, ");
    Check("Hebrew with period",Lang.En,"תודה רבה. ","he:תודה רבה. ");
    Check("English then forgot to switch",Lang.En,"see the דוח ","en:see the ","he:דוח ");
    Check("Gershayim דו\"ח",Lang.En,"דו\"ח ","he:דו\"ח ");
    Check("Prefix והמייל",Lang.En,"שלחתי את המייל ","he:שלחתי את המייל ");
    Check("Enter fixes",Lang.He,"thanks\n","en:thanks\n");
    Check("Hotkey flips whole run",Lang.En,"קפשע גקךקד", "he:קפשע גקךקד","en:\x01");
    Check("Undo auto-fix",Lang.He,"יקךךם ","en:hello ","en:\x01");
    Check("After undo, not fixed again",Lang.He,"יקךךם יקךךם ","en:hello ","en:\x01","en:hello ");
    Check("Mixed: English then Hebrew name",Lang.En,"send to דני ","en:send to ","he:דני ");
    Check("Hebrew sentence started wrong",Lang.En,"אני רוצה לשלוח את הדוח היום ","he:אני רוצה לשלוח את הדוח היום ");
    Check("English sentence started wrong",Lang.He,"I want to send the report today ","en:I want to send the report today ");
    Check("Quoted English in Hebrew layout",Lang.He,"\"hello\" ","en:\"hello\" ");
    Check("Backspace mid word",Lang.En,"שלום ","he:שלוט","en:\b","he:ם ");
    Check("Code/URL untouched",Lang.En,"example.co.il ","en:example.co.il ");
    Check("Single letter untouched",Lang.En,"a ","en:a ");
    Console.WriteLine(fails==0?"ALL SCENARIOS PASS":fails+" FAILED");
    // Realistic, correctly-typed mixed text: nothing should be changed
    string[] texts = {
      "היי דנה, מצרפת את הדוח החודשי של הלקוחות. אשמח אם תעברי על הנתונים לפני הפגישה מחר בבוקר. תודה!",
      "Hi team, please find attached the monthly report. Let me know if you have any questions before tomorrow's meeting.",
      "שלחתי את הקובץ ב Excel עם ה pivot החדש, תבדקי שהעמודות של SAP תואמות.",
      "The client asked about השינוי בדוח, can you check with Yossi?",
      "נא לעדכן את ה status ב Jira עד סוף השבוע. בהצלחה לכולם",
      "We need to update the dashboard KPIs and send it to the managers by Thursday, ok?",
      "אני בפגישה עד 14:00, אחזור אליך אחר כך",
      "As discussed, the new process starts on Sunday. Please confirm.",
    };
    int changed=0;
    foreach(var t in texts){
      lx.UserWords.Clear(); var sim=new Sim(new RunTracker(lx,new Settings()),Lang.En);
      foreach(var tok in System.Text.RegularExpressions.Regex.Split(t,"(?<= )")){
        Lang l = tok.Any(ch=>KeyMap.IsHebrewLetter(ch))?Lang.He:(tok.Any(ch=>KeyMap.IsLatin(ch))?Lang.En:sim.Layout);
        sim.Layout=l; sim.Type(tok,l);
      }
      sim.Type(" ",sim.Layout);
      bool same=sim.Screen.ToString().TrimEnd()==t.TrimEnd();
      if(!same||sim.Log.Count>0){changed++; Console.WriteLine("CHANGED: "+t+"\n   => "+sim.Screen+"  "+string.Join(" | ",sim.Log));}
    }
    Console.WriteLine(changed==0?"MIXED TEXT: no false fixes in "+texts.Length+" passages":"MIXED TEXT: "+changed+" passages changed");
    // Same passages typed entirely in the WRONG layout from the start
    int fixedOk=0;
    foreach(var t in texts){
      lx.UserWords.Clear();
      Lang first = t.Any(ch=>KeyMap.IsHebrewLetter(ch)) && KeyMap.IsHebrewLetter(t.First(ch=>KeyMap.IsHebrewLetter(ch)||KeyMap.IsLatin(ch)))?Lang.He:Lang.En;
      var sim=new Sim(new RunTracker(lx,new Settings()), first==Lang.He?Lang.En:Lang.He);
      // the user keeps typing; once fixed the layout follows; for mixed text they switch manually at language changes
      bool started=false;
      foreach(var tok in System.Text.RegularExpressions.Regex.Split(t,"(?<= )")){
        Lang l = tok.Any(ch=>KeyMap.IsHebrewLetter(ch))?Lang.He:(tok.Any(ch=>KeyMap.IsLatin(ch))?Lang.En:sim.Layout);
        if(started && l!=sim.Layout) sim.Layout=l; // deliberate switch
        if(tok.Trim().Length>0 && (KeyMap.IsLatin(tok.Trim()[0])||KeyMap.IsHebrewLetter(tok.Trim()[0]))) started=true;
        sim.Type(tok,l);
      }
      sim.Type(" ",sim.Layout);
      bool ok=sim.Screen.ToString().TrimEnd()==t.TrimEnd(); if(ok) fixedOk++;
      Console.WriteLine((ok?"RECOVERED: ":"PARTIAL:   ")+sim.Screen.ToString().TrimEnd());
    }
    Console.WriteLine("WRONG-START: "+fixedOk+"/"+texts.Length+" fully recovered");
    // Statistics over frequent words
    Stats(Lang.He,"he.txt.gz",5000); Stats(Lang.En,"en.txt.gz",5000);
  }
  static void Stats(Lang lang,string file,int n){
    var tr=new RunTracker(lx,new Settings()); Lang other=lang==Lang.He?Lang.En:Lang.He;
    double wTot=0,wRec=0,wFp=0; int rec=0,fp=0,cnt=0; var fps=new List<string>(); var miss=new List<string>();
    foreach(var line in Lines(file).Take(n)){
      var parts=line.Split(' '); string w=parts[0]; double f=double.Parse(parts[1]);
      bool okw=true; var units=new List<Unit>();
      foreach(char c in w){int vk;bool sh; if(!KeyMap.KeyFor(c,lang,out vk,out sh)||!KeyMap.IsWordKey(vk,sh)){okw=false;break;} units.Add(new Unit(UnitKind.Word,vk,sh,lang));}
      if(!okw||w.Length<2) continue; cnt++; wTot+=f;
      string inLang=string.Concat(units.Select(u=>u.CharIn(lang))), inOther=string.Concat(units.Select(u=>u.CharIn(other)));
      double a,b;
      // wrong layout: typed in `other`, meant `lang`
      if(tr.ShouldFix(inOther,inLang,other,out a,out b)){rec++;wRec+=f;} else if(miss.Count<25) miss.Add(w+"("+inOther+")");
      // right layout: typed in `lang`; would we wrongly flip it?
      if(tr.ShouldFix(inLang,inOther,lang,out a,out b)){fp++;wFp+=f; if(fps.Count<40) fps.Add(w+"->"+inOther);}
    }
    Console.WriteLine($"\n{lang} top {n}: words={cnt}  caught-when-wrong={100.0*rec/cnt:F1}% (by usage {100*wRec/wTot:F1}%)  false-flips-when-right={fp} ({100*wFp/wTot:F2}% of usage)");
    Console.WriteLine(" missed e.g.: "+string.Join(", ",miss));
    Console.WriteLine(" false flips: "+string.Join(", ",fps));
  }
}
