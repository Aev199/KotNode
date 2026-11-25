// Native HLR adapter for ObjectARX 2021. API usage follows the HLR sample
// in the Autodesk SDK and the public HLRNetSample; no SDK files are shipped.
#pragma pack(push,8)
#include <windows.h>
#include <map>
#using <mscorlib.dll>
#using <System.dll>
#using <System.Core.dll>
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
        static List<Tuple<Entity^,bool>^>^ Project(array<Solid3d^>^ solids,bool hidden,[System::Runtime::InteropServices::Out] String^% report)
        {
            if(solids==nullptr||solids->Length==0)throw gcnew ArgumentException("No solids");
            AsdkHlrCollector collector;
            collector.setDeleteState(false);
            for each(Solid3d^ solid in solids)
            {
                if(solid==nullptr)throw gcnew ArgumentNullException("solid");
                auto entity=static_cast<AcDbEntity*>(solid->UnmanagedObject.ToPointer());
                auto id=entity->objectId();
                if(id==AcDbObjectId::kNull)throw gcnew ArgumentException("HLR input must have a database ObjectId");
                collector.addEntity(id);
            }
            // Always request all visibility classes, then classify explicitly.
            int flags=kProject|kEntity|kBlock|kSubentity|kShowAll|kProcessAll|kProgress;
            AcGePoint3d target(0,0,0);
            AcGeVector3d direction(0,0,1);
            AsdkHlrEngine engine(target,direction,flags);
            engine.setAcisConversionProgressCallBack(HlrProgress);
            engine.setAcadConversionProgressCallBack(HlrProgress);
            engine.setAhlProgressCallBack(HlrProgress);
            auto status=engine.run(collector);
            GC::KeepAlive(solids);
            if(status!=Acad::eOk)throw gcnew InvalidOperationException(String::Format("HLR status {0}",(int)status));
            auto result=gcnew List<Tuple<Entity^,bool>^>();
            int visible=0,hiddenCount=0,occluded=0,internalCount=0,undefined=0,nullCount=0,points=0;
            try
            {
                for(int i=0;i<collector.mOutputData.logicalLength();++i)
                {
                    auto data=collector.mOutputData[i];
                    if(!data){++nullCount;continue;}
                    auto visibility=data->getVisibility();
                    switch(visibility)
                    {
                        case AsdkHlrData::kVisible: ++visible;break;
                        case AsdkHlrData::kHidden: ++hiddenCount;break;
                        case AsdkHlrData::kOccluded: ++occluded;continue;
                        case AsdkHlrData::kInternallyHidden: ++internalCount;continue;
                        default: ++undefined;continue;
                    }
                    bool obscured=visibility==AsdkHlrData::kHidden;
                    if(obscured&&!hidden)continue;
                    auto p=data->getResultEntity();
                    if(!p){++nullCount;continue;}
                    if(!AcDbCurve::cast(p))
                    {
                        if(AcDbPoint::cast(p)){++points;continue;}
                        throw gcnew InvalidOperationException("HLR returned a non-curve entity");
                    }
                    // The collector owns its output. Give managed code an independent copy.
                    auto borrowed=safe_cast<Entity^>(Entity::Create(IntPtr(p),false));
                    auto copy=safe_cast<Entity^>(borrowed->Clone());
                    delete borrowed;
                    try{result->Add(gcnew Tuple<Entity^,bool>(copy,obscured));}
                    catch(...){delete copy;throw;}
                }
                report=String::Format("ids={0}; raw={1}; visible={2}; hidden={3}; occluded={4}; internal={5}; undefined={6}; null={7}; points={8}; returned={9}",
                    gcnew array<Object^>{collector.getInputEntityIds().logicalLength(),collector.mOutputData.logicalLength(),visible,hiddenCount,occluded,internalCount,undefined,nullCount,points,result->Count});
                if(undefined>0)throw gcnew InvalidOperationException(report);
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
