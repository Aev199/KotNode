using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KotNode.Core
{
    // Initial subset of ГОСТ 35087-2024, table 1. Parallel flanges only.
    // Source: https://meganorm.ru/mega_doc/norm_update_04112024/gost_gosudarstvennyj-standart/0/gost_35087-2024_mezhgosudarstvennyy_standart_dvutavry.html
    // Published base table; corrections must be checked before production release.
    public static class BeamCatalog
    {
        public const string Standard="ГОСТ 35087-2024";
        // profile h b s t r A(cm²) kg/m
        static readonly string[] Rows={
            "10Б1 100 55 4.1 5.7 7 10.32 8.10",
            "12Б1 117.6 64 3.8 5.1 7 11.03 8.70",
            "12Б2 120 64 4.4 6.3 7 13.21 10.40",
            "14Б1 137.4 73 3.8 5.6 7 13.39 10.50",
            "14Б2 140 73 4.7 6.9 7 16.43 12.90",
            "16Б1 157 82 4 5.9 9 16.18 12.70",
            "16Б2 160 82 5 7.4 9 20.09 15.80",
            "18Б1 177 91 4.3 6.5 9 19.58 15.40",
            "18Б2 180 91 5.3 8 9 23.95 18.80",
            "20Б1 200 100 5.5 8 11 27.16 21.30",
            "20Б2 203 101 6.5 9.5 11 32.19 25.30",
            "20Б3 208 102 8 12 11 40.24 31.60",
            "25Б1 248 124 5 8 12 32.68 25.70",
            "25Б2 250 125 6 9 12 37.66 29.60",
            "25Б3 255 126 7.5 11.5 12 47.62 37.40",
            "25Б4 260 127 9 14 12 57.68 45.30",
            "30Б1 298 149 5.5 8 13 40.80 32.00",
            "30Б2 300 150 6.5 9 13 46.78 36.70",
            "30Б3 305 151 8 11.5 13 58.74 46.10",
            "30Б4 310 152 9.5 14 13 70.80 55.60",
            "35Б1 346 174 6 9 14 52.68 41.40",
            "35Б2 350 175 7 11 14 63.14 49.60",
            "20Ш0 190 149 5 7 13 31.11 24.40",
            "20Ш1 194 150 6 9 13 39.01 30.60",
            "20Ш2 199 151 7.5 11.5 13 49.38 38.80",
            "25Ш1 244 175 7 11 16 56.24 44.20",
            "25Ш2 249 176 8.5 13.5 16 68.59 53.80",
            "40Ш1 383 299 9.5 12.5 22 112.91 88.60",
            "40Ш2 390 300 10 16 22 135.95 106.70",
            "40Ш3 397 302 12 19.5 22 164.89 129.40",
            "40Ш4 406 304 14.5 24 22 201.98 158.60",
            "45Ш1 440 300 11 18 24 157.38 123.60",
            "50Ш4 499 300 16.5 23.5 26 221.38 173.80",
            "60Ш1 582 300 12 17 28 174.49 137.00",
            "60Ш2 589 300 16 20.5 28 217.41 170.70",
            "60Ш3 597 300 18 24.5 28 252.37 198.10",
            "70Ш1 692 300 13 20 28 211.49 166.00",
            "30К1 298 299 9 14 18 110.80 87.00",
            "30К2 300 300 10 15 18 119.78 94.00",
            "30К3 300 305 15 15 18 134.78 105.80",
            "40К1 394 398 11 18 22 186.81 146.70",
            "40К2 400 400 13 21 22 218.69 171.70",
            "40К3 406 403 16 24 22 254.87 200.10"
        };
        public static List<string> Profiles()=>Rows.Select(row=>row.Split(' ')[0]).ToList();
        public static IBeamShape Create(string profile,double length)
        {
            var row=Rows.Select(s=>s.Split(' ')).SingleOrDefault(v=>v[0]==profile);
            if(row==null)throw new ArgumentException("Профиль отсутствует в текущей выборке сортамента.");
            double N(int i)=>double.Parse(row[i],CultureInfo.InvariantCulture);
            var b=new IBeamShape{Profile=row[0],Height=N(1),Width=N(2),WebThickness=N(3),FlangeThickness=N(4),
                RootRadius=N(5),CatalogAreaMm2=(double)(decimal.Parse(row[6],CultureInfo.InvariantCulture)*100m),CatalogMassKgPerM=N(7),CatalogStandard=Standard,Length=length};
            b.Validate();return b;
        }
    }
}
