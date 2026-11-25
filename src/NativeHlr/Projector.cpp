// Native HLR adapter for ObjectARX 2021. API usage follows the HLR sample
// in the Autodesk SDK and the public HLRNetSample; no SDK files are shipped.
#pragma pack(push,8)
#include <windows.h>
#include <map>
#using <mscorlib.dll>
#using <System.dll>
#using <AcDbMgd.dll>
#define _HLR_SUPPORT_
#include "dbxHeaders.h"
#pragma pack(pop)

using namespace System;
using namespace System::Collections::Generic;
using namespace Autodesk::AutoCAD::DatabaseServices;

#pragma managed(push,off)
static void HlrProgress(int) {}
#pragma managed(pop)

namespace KotNode { namespace Native {
    public ref class Projector abstract sealed
    {
    public:
        static List<Tuple<Entity^,bool>^>^ Project(array<Solid3d^>^ solids,bool hidden)
        {
            if(solids==nullptr||solids->Length==0)throw gcnew ArgumentException("No solids");
            AsdkHlrCollector collector;
            collector.setDeleteState(false);
            for each(Solid3d^ solid in solids)
            {
                if(solid==nullptr)throw gcnew ArgumentNullException("solid");
                collector.addEntity(static_cast<AcDbEntity*>(solid->UnmanagedObject.ToPointer()));
            }
            int flags=kProject|kEntity|kBlock|kHideTangents|kHonorInternals|kProgress;
            if(hidden)flags|=kShowAll;
            AsdkHlrEngine engine(AcGePoint3d(0,0,0),AcGeVector3d(0,0,1),flags);
            engine.setAcisConversionProgressCallBack(HlrProgress);
            engine.setAcadConversionProgressCallBack(HlrProgress);
            engine.setAhlProgressCallBack(HlrProgress);
            auto status=engine.run(collector);
            GC::KeepAlive(solids);
            if(status!=Acad::eOk)throw gcnew InvalidOperationException(String::Format("HLR status {0}",(int)status));
            auto result=gcnew List<Tuple<Entity^,bool>^>();
            try
            {
                for(int i=0;i<collector.mOutputData.logicalLength();++i)
                {
                    auto data=collector.mOutputData[i];
                    if(!data)continue;
                    auto visibility=data->getVisibility();
                    if(visibility==AsdkHlrData::kUndefined)
                        throw gcnew InvalidOperationException("HLR returned undefined visibility");
                    if(visibility==AsdkHlrData::kOccluded||visibility==AsdkHlrData::kInternallyHidden)continue;
                    bool obscured=visibility!=AsdkHlrData::kVisible;
                    if(obscured&&!hidden)continue;
                    auto p=data->getResultEntity();
                    if(!p)continue;
                    if(!AcDbCurve::cast(p))
                    {
                        if(AcDbPoint::cast(p))continue;
                        throw gcnew InvalidOperationException("HLR returned a non-curve entity");
                    }
                    // The collector owns its output. Give managed code an independent copy.
                    auto borrowed=safe_cast<Entity^>(Entity::Create(IntPtr(p),false));
                    auto copy=safe_cast<Entity^>(borrowed->Clone());
                    delete borrowed;
                    try{result->Add(gcnew Tuple<Entity^,bool>(copy,obscured));}
                    catch(...){delete copy;throw;}
                }
                return result;
            }
            catch(...)
            {
                for each(auto item in result)delete item->Item1;
                throw;
            }
        }
    };
}}
